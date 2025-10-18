using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

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
    }
}
