using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using EPApi.Services.Billing; // ITrialProvisioner

namespace EPApi.Services
{
    public sealed class RegistrationService : IRegistrationService
    {
        private readonly string _cs;
        private readonly ITrialProvisioner _trial;

        public RegistrationService(IConfiguration cfg, ITrialProvisioner trial)
        {
            _cs = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
            _trial = trial;
        }

        public async Task<Guid> CreateOrgAndMembershipAndTrialAsync(
            int userId,
            string? orgName,
            CancellationToken ct = default)
        {
            var orgId = Guid.NewGuid();

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(ct);

            // ⚠️ Ajusta columnas requeridas si tu tabla orgs tiene más campos NOT NULL
            const string SQL_INSERT_ORG = @"
INSERT INTO dbo.orgs (id, name)
VALUES (@id, @name);";

            await using (var cmd = new SqlCommand(SQL_INSERT_ORG, cn, (SqlTransaction)tx))
            {
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 200) { Value = (object?)orgName ?? DBNull.Value });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            const string SQL_INSERT_USER_ORG = @"
INSERT INTO dbo.org_members (org_id, user_id, role)
VALUES (@org, @uid, @role);";

            await using (var cmd = new SqlCommand(SQL_INSERT_USER_ORG, cn, (SqlTransaction)tx))
            {
                cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
                cmd.Parameters.Add(new SqlParameter("@role", SqlDbType.NVarChar, 50) { Value = "owner" });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            // 🚀 Otorga Trial con la interfaz que EXISTE en tu código
            await _trial.EnsureTrialAsync(orgId, ct);

            return orgId;
        }
    }
}
