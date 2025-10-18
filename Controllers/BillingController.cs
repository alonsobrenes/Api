using EPApi.DataAccess;
using EPApi.Services.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public sealed class BillingController : ControllerBase
    {
        private readonly ILogger<BillingController> _logger;
        private readonly IConfiguration _cfg;
        private readonly IHostEnvironment _env;
        private readonly BillingRepository _billingRepo;
        private readonly IUsageService _usage;
        private readonly IBillingGateway _gateway;
        private readonly BillingOrchestrator _orchestrator;
        private readonly ITrialProvisioner _trialProvisioner;

        public BillingController(
            IConfiguration cfg,
            IHostEnvironment env,
            BillingRepository billingRepo,
            IUsageService usage,
            IBillingGateway gateway,
            BillingOrchestrator orchestrator,
            ILogger<BillingController> logger,
            ITrialProvisioner trialProvisioner)
        {
            _cfg = cfg;
            _env = env;
            _billingRepo = billingRepo;
            _usage = usage;
            _gateway = gateway;
            _orchestrator = orchestrator;
            _logger = logger;
            _trialProvisioner = trialProvisioner;
        }

        public sealed record EntitlementDto(string feature, int? limit, int used, int remaining);
        public sealed record SubscriptionStatusDto(
            string planCode,
            string status,
            DateTime? periodStartUtc,
            DateTime? periodEndUtc,
            IReadOnlyList<EntitlementDto> entitlements
        );
        public sealed record PlanDto(string code, string name, decimal monthlyUsd, IReadOnlyList<string> features);
        public sealed record CheckoutRequest(string planCode, int seats = 1);
        public sealed record CheckoutResponse(string checkoutUrl);

        private bool TryGetUserId(out int uid)
        {
            uid = 0;
            var c =
                User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value ??
                User.FindFirst("sub")?.Value ??
                User.FindFirst("nameid")?.Value;

            return int.TryParse(c, out uid);
        }

        private async Task<(bool ok, Guid orgId)> TryGetOrgIdAsync(CancellationToken ct)
        {
            if (TryGetUserId(out var uid))
            {
                var org = await _billingRepo.GetOrgIdForUserAsync(uid, ct);
                if (org is not null) return (true, org.Value);
            }

            if (_env.IsDevelopment())
            {
                var dev = _cfg["Billing:DevOrgId"];
                if (Guid.TryParse(dev, out var g)) return (true, g);
            }

            return (false, Guid.Empty);
        }

        [HttpGet("plans")]
        public async Task<ActionResult<IReadOnlyList<PlanDto>>> GetPlans(CancellationToken ct)
        {
            // Centralizado en BillingRepository
            var plans = await _billingRepo.GetPublicPlansAsync(ct);
            return Ok(plans);
        }

        [HttpGet("subscription")]
        public async Task<ActionResult<SubscriptionStatusDto>> GetSubscriptionStatus(CancellationToken ct)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok) return Unauthorized(new { message = "Auth requerida o Billing:DevOrgId en Development." });
            var orgId = orgTry.orgId;

            await _trialProvisioner.EnsureTrialAsync(orgId, ct);

            var (startUtc, endUtc) = await _usage.GetCurrentPeriodUtcAsync(orgId, ct);

            string planCode = "solo";
            string status = "active";

            await using (var cn = new SqlConnection(_cfg.GetConnectionString("Default")!))
            {
                await cn.OpenAsync(ct);
                const string subSql = @"
SELECT TOP (1) plan_code, status, current_period_start_utc, current_period_end_utc
FROM dbo.subscriptions
WHERE org_id=@o
ORDER BY updated_at_utc DESC";
                await using var cmd = new SqlCommand(subSql, cn);
                cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.UniqueIdentifier) { Value = orgId });
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                {
                    planCode = rd.IsDBNull(0) ? "solo" : rd.GetString(0);
                    status = rd.IsDBNull(1) ? "active" : rd.GetString(1);
                    var s = rd.IsDBNull(2) ? (DateTime?)null : rd.GetDateTime(2);
                    var e = rd.IsDBNull(3) ? (DateTime?)null : rd.GetDateTime(3);
                    if (s.HasValue && e.HasValue) { startUtc = s.Value; endUtc = e.Value; }
                }
            }

            var features = new[] { "ai.credits.monthly", "stt.minutes.monthly", "tests.auto.monthly", "sacks.monthly", "seats", "storage.gb" };
            var list = new List<EntitlementDto>(features.Length);

            foreach (var f in features)
            {
                var limit = await _billingRepo.GetEntitlementLimitAsync(orgId, f, ct);
                var used = await _billingRepo.GetUsageForPeriodAsync(orgId, f, startUtc, endUtc, ct);
                var remaining = limit.HasValue ? Math.Max(0, limit.Value - used) : int.MaxValue;
                list.Add(new EntitlementDto(f, limit, used, remaining));
            }

            return Ok(new SubscriptionStatusDto(planCode, status, startUtc, endUtc, list));
        }

        [HttpPost("checkout")]
        public async Task<ActionResult<CheckoutResponse>> CreateCheckout([FromBody] CheckoutRequest body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body.planCode))
                return BadRequest(new { message = "planCode requerido" });

            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok) return Unauthorized(new { message = "Auth requerida o Billing:DevOrgId en Development." });
            var orgId = orgTry.orgId;

            var mode = _cfg["Billing:Mode"] ?? "Simulated";
            if (string.Equals(mode, "Gateway", StringComparison.OrdinalIgnoreCase))
            {
                var returnBase = _cfg["Billing:ReturnUrlBase"] ?? "http://localhost:5173";
                var returnUrl = $"{returnBase}/account/billing/return";
                var checkoutRedirectUrl = await _gateway.CreateCheckoutSession(orgId, body.planCode, returnUrl, ct);
                return Ok(new CheckoutResponse(checkoutRedirectUrl));
            }

            // Entitlements desde Repo
            var map = await _billingRepo.GetEntitlementsByPlanCodeAsync(body.planCode, ct);
            if (map.Count == 0) return BadRequest(new { message = "planCode inválido" });

            var now = DateTime.UtcNow;
            var startUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var endUtc = startUtc.AddMonths(1);

            await _orchestrator.ApplySubscriptionAndEntitlementsAsync(
                orgId: orgId,
                planCodeLower: body.planCode.ToLowerInvariant(),
                statusLower: "active",
                startUtc: startUtc,
                endUtc: endUtc,
                entitlements: map,
                ct: ct);

            var url = $"/account/billing/thanks?plan={body.planCode.ToLowerInvariant()}";
            return Ok(new CheckoutResponse(url));
        }

        [HttpPost("portal")]
        public async Task<ActionResult<CheckoutResponse>> CreatePortal(CancellationToken ct)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok) return Unauthorized(new { message = "Auth requerida o Billing:DevOrgId en Development." });
            var url = await _gateway.GetCustomerPortalUrl(orgTry.orgId, ct);
            return Ok(new CheckoutResponse(url));
        }

        [AllowAnonymous]
        [HttpPost("webhook")]
        public async Task<IActionResult> Webhook(CancellationToken ct)
        {
            var wcfg = _cfg.GetSection("Billing:Webhook");
            bool verify = string.Equals(wcfg["Verify"], "true", StringComparison.OrdinalIgnoreCase);
            if (verify)
            {
                Request.EnableBuffering();
                string rawBody;
                using (var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                {
                    rawBody = await reader.ReadToEndAsync();
                }
                Request.Body.Position = 0;

                var headerName = wcfg["Header"] ?? "X-Signature";
                var tsHeaderName = wcfg["TimestampHeader"];
                var secret1 = wcfg["Secret"];
                var secret2 = wcfg["Secret2"];
                var allowBodyOnly = string.Equals(wcfg["AllowBodyOnly"], "true", StringComparison.OrdinalIgnoreCase);
                var alg = wcfg["Algorithm"] ?? "HMACSHA256";
                var skew = int.TryParse(wcfg["SkewSeconds"], out var s) ? s : 300;

                var sigHeader = Request.Headers[headerName].ToString();
                if (string.IsNullOrWhiteSpace(sigHeader) || (string.IsNullOrWhiteSpace(secret1) && string.IsNullOrWhiteSpace(secret2)))
                    return Unauthorized(new { message = "Missing signature or secret" });

                string? ts = null;
                long unix = 0;
                bool requireTs = !string.IsNullOrEmpty(tsHeaderName);
                if (requireTs)
                {
                    ts = Request.Headers[tsHeaderName].ToString();
                    if (string.IsNullOrWhiteSpace(ts) && !allowBodyOnly) return Unauthorized(new { message = "Missing timestamp" });
                    if (!string.IsNullOrWhiteSpace(ts))
                    {
                        if (!long.TryParse(ts, out unix)) return Unauthorized(new { message = "Invalid timestamp" });
                        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (Math.Abs(now - unix) > skew) return Unauthorized(new { message = "Stale timestamp" });
                    }
                }

                var basesToSign = new List<string>();
                if (!string.IsNullOrWhiteSpace(ts)) basesToSign.Add($"{ts}.{rawBody}");
                if (allowBodyOnly || !requireTs) basesToSign.Add(rawBody);
                if (!string.Equals(alg, "HMACSHA256", StringComparison.OrdinalIgnoreCase))
                    return Unauthorized(new { message = $"Unsupported algorithm: {alg}" });

                static bool ConstantTimeEquals(string a, string b)
                {
                    if (a.Length != b.Length) return false;
                    int diff = 0;
                    for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
                    return diff == 0;
                }

                bool valid = false;
                foreach (var b in basesToSign)
                {
                    if (valid) break;
                    foreach (var sec in new[] { secret1, secret2 })
                    {
                        if (string.IsNullOrWhiteSpace(sec)) continue;
                        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(sec!));
                        var mac = h.ComputeHash(Encoding.UTF8.GetBytes(b));
                        var computed = BitConverter.ToString(mac).Replace("-", "").ToLowerInvariant();
                        if (ConstantTimeEquals(computed, sigHeader.Trim().ToLowerInvariant()))
                        {
                            valid = true;
                            break;
                        }
                    }
                }
                if (!valid) return Unauthorized(new { message = "Invalid signature" });
            }

            var events = await _gateway.ParseWebhook(Request, ct);
            _logger.LogInformation("Webhook parsed events: count={Count}", events.Count);

            int applied = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                var ps = e.PeriodStartUtc ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var pe = e.PeriodEndUtc ?? ps.AddMonths(1);

                var plan = (e.PlanCode ?? "").ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(plan)) continue;

                var ent = await _billingRepo.GetEntitlementsByPlanCodeAsync(plan, ct);
                if (ent.Count == 0) continue;

                var key = e.IdempotencyKey ?? $"{(e.Provider ?? "Fake").ToLowerInvariant()}:{e.OrgId}:{e.Kind}:{plan}:{ps:O}:{pe:O}";
                if (key.Length > 200) key = key.Substring(0, 200);

                try
                {
                    var csIdem = _cfg.GetConnectionString("Default")!;
                    await using var cnIdem = new SqlConnection(csIdem);
                    await cnIdem.OpenAsync(ct);
                    var cmdIdem = new SqlCommand(@"
INSERT INTO dbo.webhook_idempotency(event_key, provider, org_id, kind, plan_code, period_start_utc, period_end_utc)
VALUES (@k, @p, @o, @kind, @plan, @ps, @pe);", cnIdem);
                    cmdIdem.Parameters.AddWithValue("@k", key);
                    cmdIdem.Parameters.AddWithValue("@p", (object?)e.Provider ?? "Fake");
                    cmdIdem.Parameters.AddWithValue("@o", e.OrgId);
                    cmdIdem.Parameters.AddWithValue("@kind", e.Kind.ToString());
                    cmdIdem.Parameters.AddWithValue("@plan", plan);
                    cmdIdem.Parameters.AddWithValue("@ps", ps);
                    cmdIdem.Parameters.AddWithValue("@pe", pe);
                    await cmdIdem.ExecuteNonQueryAsync(ct);
                }
                catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
                {
                    _logger.LogInformation("Idempotency duplicate: {Key}", key);
                    continue;
                }

                var status = string.IsNullOrWhiteSpace(e.Status) ? "active" : e.Status.ToLowerInvariant();
                await _orchestrator.ApplySubscriptionAndEntitlementsAsync(e.OrgId, plan, status, ps, pe, ent, ct);
                applied++;
            }

            return Ok(new { received = events.Count, applied });
        }
    }
}
