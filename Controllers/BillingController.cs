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
            ITrialProvisioner trialProvisioner  )
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

        // -----------------------------
        // DTOs internos a este controller
        // -----------------------------
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

        // -----------------------------
        // Helpers de auth/org (con fallback DEV)
        // -----------------------------
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
            // 1) Si hay usuario autenticado, buscar su org
            if (TryGetUserId(out var uid))
            {
                var org = await _billingRepo.GetOrgIdForUserAsync(uid, ct);
                if (org is not null) return (true, org.Value);
            }

            // 2) Fallback solo en Development: Billing:DevOrgId
            if (_env.IsDevelopment())
            {
                var dev = _cfg["Billing:DevOrgId"];
                if (Guid.TryParse(dev, out var g)) return (true, g);
            }

            return (false, Guid.Empty);
        }

        // -----------------------------
        // GET /api/billing/plans
        // -----------------------------
        [HttpGet("plans")]
        public ActionResult<IReadOnlyList<PlanDto>> GetPlans()
        {
            var plans = new List<PlanDto>
            {
                new("solo",   "Solo",   29m,  new []{ "200 opiniones IA/mes", "100 tests auto/mes", "20 SACKS/mes", "1 seat", "10 Gb Almacenamiento" }),
                new("clinic", "Clínica", 99m,  new []{ "1000 opiniones IA/mes", "500 tests auto/mes", "100 SACKS/mes", "5 seats", "50 Gb Almacenamiento" }),
                new("pro",    "Pro",    299m, new []{ "5000 opiniones IA/mes", "2000 tests auto/mes", "300 SACKS/mes", "20 seats", "200 Gb Almacenamiento" }),
            };
            return Ok(plans);
        }

        // -----------------------------
        // GET /api/billing/subscription
        // -----------------------------
        [HttpGet("subscription")]
        public async Task<ActionResult<SubscriptionStatusDto>> GetSubscriptionStatus(CancellationToken ct)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok) return Unauthorized(new { message = "Auth requerida o Billing:DevOrgId en Development." });
            var orgId = orgTry.orgId;

            await _trialProvisioner.EnsureTrialAsync(orgId, ct);

            // período vigente (suscripción) o mes calendario (fallback)
            var (startUtc, endUtc) = await _usage.GetCurrentPeriodUtcAsync(orgId, ct);

            // lee plan/status actuales si existen
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

            // compila entitlements reportados (incluye storage.gb)
            var features = new[] { "ai.credits.monthly", "tests.auto.monthly", "sacks.monthly", "storage.gb" };
            var list = new List<EntitlementDto>(features.Length);

            foreach (var f in features)
            {
                var limit = await _billingRepo.GetEntitlementLimitAsync(orgId, f, ct); // null = ilimitado
                var used = await _billingRepo.GetUsageForPeriodAsync(orgId, f, startUtc, endUtc, ct);
                var remaining = limit.HasValue ? Math.Max(0, limit.Value - used) : int.MaxValue;
                list.Add(new EntitlementDto(f, limit, used, remaining));
            }

            return Ok(new SubscriptionStatusDto(planCode, status, startUtc, endUtc, list));
        }

        // -----------------------------
        // POST /api/billing/checkout  (DUMMY: simula cambio de plan)
        // -----------------------------
        [HttpPost("checkout")]
        public async Task<ActionResult<CheckoutResponse>> CreateCheckout([FromBody] CheckoutRequest body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body.planCode))
                return BadRequest(new { message = "planCode requerido" });

            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok) return Unauthorized(new { message = "Auth requerida o Billing:DevOrgId en Development." });
            var orgId = orgTry.orgId;

            var mode = _cfg["Billing:Mode"] ?? "Simulated"; // "Gateway" | "Simulated"
            if (string.Equals(mode, "Gateway", StringComparison.OrdinalIgnoreCase))
            {
                var returnBase = _cfg["Billing:ReturnUrlBase"] ?? "http://localhost:5173";
                var returnUrl = $"{returnBase}/account/billing/return";
                var checkoutRedirectUrl = await _gateway.CreateCheckoutSession(orgId, body.planCode, returnUrl, ct);
                return Ok(new CheckoutResponse(checkoutRedirectUrl));
            }

            var map = body.planCode.ToLowerInvariant() switch
            {
                "solo" => new Dictionary<string, int> { ["ai.credits.monthly"] = 200, ["tests.auto.monthly"] = 100, ["sacks.monthly"] = 20, ["seats"] = 1, ["storage.gb"] = 10 },
                "clinic" => new Dictionary<string, int> { ["ai.credits.monthly"] = 1000, ["tests.auto.monthly"] = 500, ["sacks.monthly"] = 100, ["seats"] = 5, ["storage.gb"] = 50 },
                "pro" => new Dictionary<string, int> { ["ai.credits.monthly"] = 5000, ["tests.auto.monthly"] = 2000, ["sacks.monthly"] = 300, ["seats"] = 20, ["storage.gb"] = 200 },
                // === NUEVO: plan trial (para tests o activaciones manuales) ===
                "trial" => new Dictionary<string, int> { ["ai.credits.monthly"] = 10, ["tests.auto.monthly"] = 2, ["sacks.monthly"] = 2, ["seats"] = 1, ["storage.gb"] = 1 },
                _ => null
            };
            if (map is null) return BadRequest(new { message = "planCode inválido" });

            // período dummy = mes calendario (cuando haya proveedor real, usa la ventana del webhook)
            var now = DateTime.UtcNow;
            var startUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var endUtc = startUtc.AddMonths(1);

            await _orchestrator.ApplySubscriptionAndEntitlementsAsync(
                orgId,
                body.planCode.ToLowerInvariant(),
                "active",
                startUtc,
                endUtc,
                map,
                ct);

            // URL dummy (en proveedor real devuelves checkoutUrl de Paddle/Tilopay)
            var url = $"/account/billing/thanks?plan={body.planCode.ToLowerInvariant()}";
            return Ok(new CheckoutResponse(url));
        }

        // -----------------------------
        // POST /api/billing/portal (dummy)
        // -----------------------------
        [HttpPost("portal")]
        public async Task<ActionResult<CheckoutResponse>> CreatePortal(CancellationToken ct)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok) return Unauthorized(new { message = "Auth requerida o Billing:DevOrgId en Development." });
            var url = await _gateway.GetCustomerPortalUrl(orgTry.orgId, ct);
            return Ok(new CheckoutResponse(url));
        }

        // -----------------------------
        // POST /api/billing/webhook 
        // -----------------------------
        [AllowAnonymous]
        [HttpPost("webhook")]
        public async Task<IActionResult> Webhook(CancellationToken ct)
        {
            // === Webhook signature verification (config-driven) ===
            var wcfg = _cfg.GetSection("Billing:Webhook");

            bool verify = string.Equals(wcfg["Verify"], "true", StringComparison.OrdinalIgnoreCase);
            if (verify)
            {
                // 1) Enable buffering to read the body twice
                Request.EnableBuffering();

                // 2) Read raw body
                string rawBody;
                using (var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                {
                    rawBody = await reader.ReadToEndAsync();
                }
                // reset for downstream parser
                Request.Body.Position = 0;

                // 3) Headers & config
                var headerName = wcfg["Header"] ?? "X-Signature";
                var tsHeaderName = wcfg["TimestampHeader"]; // opcional
                var secret1 = wcfg["Secret"];
                var secret2 = wcfg["Secret2"];         // opcional (rotación)
                var allowBodyOnly = string.Equals(wcfg["AllowBodyOnly"], "true", StringComparison.OrdinalIgnoreCase);
                var alg = wcfg["Algorithm"] ?? "HMACSHA256";
                var skew = int.TryParse(wcfg["SkewSeconds"], out var s) ? s : 300;

                var sigHeader = Request.Headers[headerName].ToString();
                if (string.IsNullOrWhiteSpace(sigHeader) || (string.IsNullOrWhiteSpace(secret1) && string.IsNullOrWhiteSpace(secret2)))
                {
                    _logger.LogWarning("Webhook signature missing or secret not configured");
                    return Unauthorized(new { message = "Missing signature or secret" });
                }

                // 3.a Timestamp (si está configurado)
                string? ts = null;
                long unix = 0;
                bool requireTs = !string.IsNullOrEmpty(tsHeaderName);
                if (requireTs)
                {
                    ts = Request.Headers[tsHeaderName].ToString();
                    if (string.IsNullOrWhiteSpace(ts) && !allowBodyOnly)
                    {
                        _logger.LogWarning("Webhook missing timestamp");
                        return Unauthorized(new { message = "Missing timestamp" });
                    }
                    if (!string.IsNullOrWhiteSpace(ts))
                    {
                        if (!long.TryParse(ts, out unix))
                        {
                            _logger.LogWarning("Webhook invalid timestamp");
                            return Unauthorized(new { message = "Invalid timestamp" });
                        }

                        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (Math.Abs(now - unix) > skew)
                        {
                            _logger.LogWarning("Webhook stale timestamp");
                            return Unauthorized(new { message = "Stale timestamp" });
                        }
                    }
                }

                // 4) Bases a firmar
                var basesToSign = new List<string>();
                if (!string.IsNullOrWhiteSpace(ts)) basesToSign.Add($"{ts}.{rawBody}");
                if (allowBodyOnly || !requireTs) basesToSign.Add(rawBody); // compat si no hay TimestampHeader

                // 5) Compute HMAC (default HMACSHA256)
                if (!string.Equals(alg, "HMACSHA256", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Webhook unsupported algorithm {Alg}", alg);
                    return Unauthorized(new { message = $"Unsupported algorithm: {alg}" });
                }

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

                if (!valid)
                {
                    _logger.LogWarning("Webhook invalid signature");
                    return Unauthorized(new { message = "Invalid signature" });
                }
            }
            // === end verification ===

            var events = await _gateway.ParseWebhook(Request, ct);
            _logger.LogInformation("Webhook parsed events: count={Count}", events.Count);

            int applied = 0;
            foreach (var e in events)
            {
                // Período: si el evento no lo trae, usamos mes calendario UTC
                var ps = e.PeriodStartUtc ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var pe = e.PeriodEndUtc ?? ps.AddMonths(1);

                // Mapa de límites (igual que en checkout Simulated)
                var plan = (e.PlanCode ?? "").ToLowerInvariant();
                IReadOnlyDictionary<string, int>? ent = plan switch
                {
                    "solo" => new Dictionary<string, int> { ["ai.credits.monthly"] = 200, ["tests.auto.monthly"] = 100, ["sacks.monthly"] = 20, ["seats"] = 1, ["storage.gb"] = 10 },
                    "clinic" => new Dictionary<string, int> { ["ai.credits.monthly"] = 1000, ["tests.auto.monthly"] = 500, ["sacks.monthly"] = 100, ["seats"] = 5, ["storage.gb"] = 50 },
                    "pro" => new Dictionary<string, int> { ["ai.credits.monthly"] = 5000, ["tests.auto.monthly"] = 2000, ["sacks.monthly"] = 300, ["seats"] = 20, ["storage.gb"] = 200 },
                    // === NUEVO: plan trial (para compatibilidad si el gateway emite trial) ===
                    "trial" => new Dictionary<string, int> { ["ai.credits.monthly"] = 10, ["tests.auto.monthly"] = 2, ["sacks.monthly"] = 2, ["seats"] = 1, ["storage.gb"] = 1 },
                    _ => null
                };
                if (ent is null) continue; // plan desconocido: ignoramos

                // Idempotencia de webhook
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
                    _logger.LogDebug("Idempotency inserted {Key}", key);
                }
                catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
                {
                    _logger.LogInformation("Idempotency duplicate: {Key}", key);
                    continue; // ya aplicado
                }

                var status = string.IsNullOrWhiteSpace(e.Status) ? "active" : e.Status.ToLowerInvariant();
                await _orchestrator.ApplySubscriptionAndEntitlementsAsync(
                    e.OrgId, plan, status, ps, pe, ent, ct);

                _logger.LogInformation("Webhook applied plan {Plan} for org {OrgId}", plan, e.OrgId);
                applied++;
            }

            return Ok(new { received = events.Count, applied });
        }
    }
}
