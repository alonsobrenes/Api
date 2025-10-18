using EPApi.DataAccess;
using EPApi.Services.Billing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Services.Billing
{
    public sealed class TrialProvisioner : ITrialProvisioner
    {
        private readonly IConfiguration _cfg;
        private readonly BillingOrchestrator _orchestrator;
        private readonly ILogger<TrialProvisioner> _logger;
        private readonly BillingRepository _billingRepo;

        public TrialProvisioner(
            IConfiguration cfg,
            BillingOrchestrator orchestrator,
            ILogger<TrialProvisioner> logger,
            BillingRepository billingRepo)
        {
            _cfg = cfg;
            _orchestrator = orchestrator;
            _logger = logger;
            _billingRepo = billingRepo;
        }

        public async Task EnsureTrialAsync(Guid orgId, CancellationToken ct = default)
        {
            if (orgId == Guid.Empty) return;

            if (await HasActiveOrTrialAsync(orgId, ct))
            {
                _logger.LogDebug("Org {OrgId} already has an active/trial subscription. Skipping trial provisioning.", orgId);
                return;
            }

            var planCode = (_cfg["Billing:TrialPlanCode"] ?? _cfg["Billing:DefaultSignupPlanCode"] ?? "solo")
                .Trim().ToLowerInvariant();

            var entitlements = await _billingRepo.GetEntitlementsByPlanCodeAsync(planCode, ct);
            if (entitlements.Count == 0)
            {
                _logger.LogWarning("Trial plan '{PlanCode}' not found in DB entitlements. Aborting trial setup.", planCode);
                return;
            }

            var now = DateTime.UtcNow;
            var periodStartUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var periodEndUtc = periodStartUtc.AddMonths(1);

            await _orchestrator.ApplySubscriptionAndEntitlementsAsync(
                orgId: orgId,
                planCodeLower: planCode,
                statusLower: "trial",
                startUtc: periodStartUtc,
                endUtc: periodEndUtc,
                entitlements: entitlements,
                ct: ct
            );

            _logger.LogInformation("Trial provisioned for Org {OrgId} with plan {PlanCode}.", orgId, planCode);
        }

        private async Task<bool> HasActiveOrTrialAsync(Guid orgId, CancellationToken ct)
        {
            var cs = _cfg.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(cs)) return false;

            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync(ct);

            const string sql = @"
SELECT TOP (1) 1
FROM dbo.subscriptions
WHERE org_id = @o
  AND status IN (N'active', N'trial')
  AND (current_period_end_utc IS NULL OR current_period_end_utc > SYSUTCDATETIME())
ORDER BY updated_at_utc DESC;";

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.UniqueIdentifier) { Value = orgId });

            var obj = await cmd.ExecuteScalarAsync(ct);
            return obj != null;
        }
    }
}
