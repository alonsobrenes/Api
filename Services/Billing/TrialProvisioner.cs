// Services/Billing/TrialProvisioner.cs
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EPApi.Services.Billing
{
    public sealed class TrialProvisioner : ITrialProvisioner
    {
        private readonly string _cs;
        private readonly BillingOrchestrator _orchestrator;
        private readonly ILogger<TrialProvisioner> _log;

        public TrialProvisioner(IConfiguration cfg, BillingOrchestrator orchestrator, ILogger<TrialProvisioner> log)
        {
            _cs = cfg.GetConnectionString("Default")
               ?? throw new InvalidOperationException("Missing Default connection string");
            _orchestrator = orchestrator;
            _log = log;
        }

        public async Task EnsureTrialAsync(Guid orgId, CancellationToken ct)
        {
            // 1) ¿La org ya tiene suscripciones?
            if (await OrganizationHasAnySubscriptionAsync(orgId, ct))
                return;

            var now = DateTime.UtcNow;
            var ends = now.AddDays(7);

            // 2) Insertar suscripción trial (provider=system, status=active)
            await InsertTrialSubscriptionAsync(orgId, now, ends, ct);

            // 3) Aplicar entitlements del plan trial
            var ent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["tests.auto.monthly"] = 2,
                ["sacks.monthly"] = 2,
                ["ai.opinion.monthly"] = 2,
                ["storage.gb"] = 1,
                ["seats"] = 1
            };

            await _orchestrator.ApplySubscriptionAndEntitlementsAsync(
                orgId: orgId,
                planCodeLower: "trial",
                statusLower: "active",
                startUtc: now,
                endUtc: ends,
                entitlements: ent,
                ct: ct
            );

            _log.LogInformation("Trial provisioned for org {OrgId} until {End}", orgId, ends);
        }

        private async Task<bool> OrganizationHasAnySubscriptionAsync(Guid orgId, CancellationToken ct)
        {
            const string sql = "SELECT TOP (1) 1 FROM dbo.subscriptions WHERE org_id=@o;";
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.UniqueIdentifier) { Value = orgId });
            var o = await cmd.ExecuteScalarAsync(ct);
            return o is not null && o != DBNull.Value;
        }

        private async Task InsertTrialSubscriptionAsync(Guid orgId, DateTime startUtc, DateTime endUtc, CancellationToken ct)
        {
            const string sql = @"
INSERT INTO dbo.subscriptions(
    id, org_id, provider, plan_code, status, 
    provider_customer_id, provider_subscription_id,
    current_period_start_utc, current_period_end_utc, created_at_utc, updated_at_utc
) VALUES (
    @id, @o, N'system', N'trial', N'active',
    NULL, NULL,
    @ps, @pe, SYSUTCDATETIME(), SYSUTCDATETIME()
);";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
            cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@ps", SqlDbType.DateTime2) { Value = startUtc });
            cmd.Parameters.Add(new SqlParameter("@pe", SqlDbType.DateTime2) { Value = endUtc });
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
