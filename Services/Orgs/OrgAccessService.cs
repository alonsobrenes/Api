using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace EPApi.Services.Orgs
{
    public sealed class OrgAccessService : IOrgAccessService
    {
        private readonly string _cs;

        public OrgAccessService(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                  ?? throw new InvalidOperationException("Missing connection string 'Default'");
        }

        public async Task<OrgMode> GetOrgModeAsync(Guid orgId, CancellationToken ct = default)
        {
            const string sql = @"
;WITH active_sub AS (
  SELECT TOP (1) s.plan_code
  FROM dbo.subscriptions s
  WHERE s.org_id = @org
    AND (s.status IN ('active','trial'))
    AND (s.current_period_start_utc IS NULL OR s.current_period_start_utc <= SYSUTCDATETIME())
    AND (s.current_period_end_utc   IS NULL OR s.current_period_end_utc   >  SYSUTCDATETIME())
  ORDER BY s.current_period_start_utc DESC, s.id DESC
)
SELECT CAST(ISNULL(bpe.limit_value, 1) AS int) AS seats
FROM active_sub a
LEFT JOIN dbo.billing_plans p
  ON p.code = a.plan_code
LEFT JOIN dbo.billing_plan_entitlements bpe
  ON bpe.plan_id = p.id
 AND bpe.feature_code = 'seats';
";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });

            var obj = await cmd.ExecuteScalarAsync(ct);
            int seats = (obj is int i) ? i : 1;
            return seats > 1 ? OrgMode.Multi : OrgMode.Solo;
        }

        public async Task<bool> IsOwnerOfMultiSeatOrgAsync(int userId, Guid orgId, CancellationToken ct = default)
        {
            const string sqlOwner = @"
SELECT 1
FROM dbo.org_members m
WHERE m.org_id = @org
  AND m.user_id = @uid
  AND m.role = 'owner';";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            bool isOwner;
            await using (var cmd = new SqlCommand(sqlOwner, cn))
            {
                cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });

                var obj = await cmd.ExecuteScalarAsync(ct);
                isOwner = obj != null;
            }

            if (!isOwner) return false;

            var mode = await GetOrgModeAsync(orgId, ct);
            return mode == OrgMode.Multi;
        }

        public async Task<bool> IsOwnerAsync(int userId, CancellationToken ct = default)
        {
            const string sqlOwner = @"
SELECT 1
FROM dbo.org_members m
WHERE m.user_id = @uid
  AND m.role = 'editor';";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            bool isOwner;
            await using (var cmd = new SqlCommand(sqlOwner, cn))
            {                
                cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });

                var obj = await cmd.ExecuteScalarAsync(ct);
                isOwner = obj == null;
            }

            return isOwner;            
        }

        public async Task<Guid?> GetSupportOrgForUserAsync(int userId, CancellationToken ct = default)
        {
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            // 1) Obtener todas las orgs activas del usuario
            const string sql = @"
SELECT m.org_id, m.role
FROM dbo.org_members m
WHERE m.user_id = @uid
  AND m.status = 'active';";

            var memberships = new List<(Guid OrgId, string Role)>();

            await using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });

                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    var orgId = rd.GetGuid(0);
                    var role = rd.IsDBNull(1) ? "" : rd.GetString(1);
                    memberships.Add((orgId, role));
                }
            }

            if (memberships.Count == 0)
                return null;

            // 2) Clasificar por modo (Solo/Multi) y rol (owner/editor)
            var multiOwners = new List<Guid>();
            var multiMembers = new List<Guid>();
            var soloOrgs = new List<Guid>();

            foreach (var m in memberships)
            {
                var mode = await GetOrgModeAsync(m.OrgId, ct); // ya existe en este servicio

                if (mode == OrgMode.Multi)
                {
                    if (string.Equals(m.Role, "owner", StringComparison.OrdinalIgnoreCase))
                    {
                        multiOwners.Add(m.OrgId);
                    }
                    else
                    {
                        // editor u otro rol: lo tratamos como miembro de clínica
                        multiMembers.Add(m.OrgId);
                    }
                }
                else
                {
                    // Org "Solo" (1 seat)
                    soloOrgs.Add(m.OrgId);
                }
            }

            // 3) Prioridades:
            //  - primero: clínicas donde es owner
            //  - si no hay: clínicas donde es editor
            //  - si no hay clínicas: única org que tenga (si solo hay una)
            //  - si hay más de una "solo" o caso raro: null (para no inventar)

            if (multiOwners.Count > 0)
                return multiOwners[0];

            if (multiMembers.Count > 0)
                return multiMembers[0];

            if (soloOrgs.Count == 1)
                return soloOrgs[0];

            // Caso ambiguo (múltiples orgs solo, etc.)
            return null;
        }

    }
}
