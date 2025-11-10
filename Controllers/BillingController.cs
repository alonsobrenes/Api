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
using EPApi.Services.Orgs;

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
        private readonly IOrgRepository _orgRepository;
        private readonly IPaymentMethodRepository _pmRepo;
        private readonly IPaymentsRepository _paymentsRepo;
        public BillingController(
            IConfiguration cfg,
            IHostEnvironment env,
            BillingRepository billingRepo,
            IUsageService usage,
            IBillingGateway gateway,
            BillingOrchestrator orchestrator,
            ILogger<BillingController> logger,
            ITrialProvisioner trialProvisioner,
            IOrgRepository orgRepository,
            IPaymentMethodRepository pmRepo,
            IPaymentsRepository paymentsRepo)
        {
            _cfg = cfg;
            _env = env;
            _billingRepo = billingRepo;
            _usage = usage;
            _gateway = gateway;
            _orchestrator = orchestrator;
            _logger = logger;
            _trialProvisioner = trialProvisioner;
            _orgRepository = orgRepository;
            _pmRepo = pmRepo;
            _paymentsRepo = paymentsRepo;
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
        
        [HttpGet("payments")]
        public async Task<IActionResult> GetPayments([FromQuery] int limit = 50, CancellationToken ct = default)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok) return Unauthorized(new { message = "Auth requerida" });
            var orgId = orgTry.orgId;

            if (limit <= 0 || limit > 200) limit = 50;

            var items = await _paymentsRepo.ListByOrgAsync(orgId, limit, ct);

            // No-cache para evitar listas desactualizadas por el navegador
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            return Ok(new { items });
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

            var (can, reason) = await _orchestrator.CanChangeToPlanAsync(orgId, body.planCode, ct);
            if (!can) return Conflict(new { code = "DOWNGRADE_BLOCKED", message = reason });

            var mode = _cfg["Billing:Mode"] ?? "Simulated";
            var returnBase = _cfg["Billing:ReturnUrlBase"] ?? "http://localhost:5173";
            var returnUrl = $"{returnBase}/account/billing/return";

            if (string.Equals(mode, "Gateway", StringComparison.OrdinalIgnoreCase))
            {
                var plan = await _billingRepo.GetPlanByCodeAsync(body.planCode, ct);
                var isProviderPlan = plan != null
                    && !string.IsNullOrWhiteSpace(plan.Provider)
                    && !string.IsNullOrWhiteSpace(plan.ProviderPriceId);

                if (isProviderPlan && string.Equals(plan!.Provider, "tilopay", StringComparison.OrdinalIgnoreCase))
                {
                    var hostedUrl = await _orchestrator.GetHostedSubscriptionUrlAsync(orgId, body.planCode, ct);
                    return Ok(new CheckoutResponse(hostedUrl));
                }

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

            var backendOrigin = _cfg["Billing:BackendBase"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(backendOrigin))
            {
                backendOrigin = "https://localhost:53793";
            }

            var simUrl = $"{backendOrigin}/api/billing/sim-checkout" +
                         $"?planCode={Uri.EscapeDataString(body.planCode)}" +
                         $"&returnUrl={Uri.EscapeDataString(returnUrl)}" +
                         $"&org={orgId}";

            return Ok(new CheckoutResponse(simUrl));            
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

        [HttpGet("payment-method")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<ActionResult<object>> GetPaymentMethod(CancellationToken ct)
        {            
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok) return Unauthorized(new { message = "Auth requerida" });
            var orgId = orgTry.orgId;

            var pm = await _pmRepo.GetActiveAsync(orgId, ct);
            if (pm == null) return Ok(new { has = false });

            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            return Ok(new
            {
                has = true,
                brand = pm.Brand,
                last4 = pm.Last4,
                expMonth = pm.ExpMonth,
                expYear = pm.ExpYear,
                provider = pm.Provider
            });
        }

        [HttpPost("payment-method/start-tokenization")]
        public async Task<ActionResult<object>> StartTokenization([FromBody] StartTokenizationRequest body, CancellationToken ct)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok) return Unauthorized(new { message = "Auth requerida" });
            var orgId = orgTry.orgId;

            var returnUrl = string.IsNullOrWhiteSpace(body?.ReturnUrl)
                ? $"{_cfg["Billing:ReturnUrlBase"]?.TrimEnd('/')}/account/billing/pm-return"
                : body!.ReturnUrl;

            var mode = _cfg["Billing:Mode"] ?? "Simulated";

            if (string.Equals(mode, "Gateway", StringComparison.OrdinalIgnoreCase))
            {
                // REAL
                var url = await _gateway.CreateTokenizationSessionAsync(orgId, returnUrl, ct);
                return Ok(new { redirectUrl = url });
            }
            else
            {
                // SIM
                var backendOrigin = Request.Scheme + "://" + Request.Host.Value;
                var url = $"{backendOrigin}/api/billing/sim-tokenize?returnUrl={Uri.EscapeDataString(returnUrl)}&org={orgId}";
                return Ok(new { redirectUrl = url });
            }
        }

        public sealed class StartTokenizationRequest
        {
            public string? ReturnUrl { get; set; }
        }

        
        // Finalize: el retorno real de TiloPay te dará un token. Guardamos token y, si existe, metadata básica.
        [HttpPost("payment-method/finalize")]
        public async Task<ActionResult> FinalizePaymentMethod([FromBody] FinalizePaymentMethodRequest body, CancellationToken ct)
        {
            var orgTry = await TryGetOrgIdAsync(ct);
            if (!orgTry.ok) return Unauthorized(new { message = "Auth requerida" });
            var orgId = orgTry.orgId;

            if (string.IsNullOrWhiteSpace(body.ProviderPmId))
                return BadRequest(new { message = "ProviderPmId (token) requerido" });


            string provider = _cfg["Billing:Gateway"] ?? "Fake";
            string mode = _cfg["Billing:Mode"] ?? "Simulated";

            string? brand = body.Brand;
            string? last4 = body.Last4;
            int? expMonth = body.ExpMonth;
            int? expYear = body.ExpYear;
            string? rawPayload = body.RawPayload;

            // Si estamos en Gateway real y no tenemos metadatos de tarjeta, intenta traerlos
            if (string.Equals(mode, "Gateway", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(provider, "TiloPay", StringComparison.OrdinalIgnoreCase) &&
                (brand == null || last4 == null || !expMonth.HasValue || !expYear.HasValue))
            {
                var details = await _gateway.TryFetchPaymentMethodDetailsAsync(body.ProviderPmId, ct);
                if (details != null)
                {
                    brand ??= details.Brand;
                    last4 ??= details.Last4;
                    expMonth ??= details.ExpMonth;
                    expYear ??= details.ExpYear;
                    // Preferimos guardar el raw más informativo
                    rawPayload = string.IsNullOrWhiteSpace(details.RawProviderPayload) ? rawPayload : details.RawProviderPayload;
                }
            }

            await _pmRepo.UpsertActiveAsync(
                orgId,
                provider: "tilopay",
                providerPmId: body.ProviderPmId,
                brand: body.Brand, 
                last4: body.Last4,
                expMonth: body.ExpMonth, 
                expYear: body.ExpYear,
                rawPayload: body.RawPayload, ct
            );

            var pm = await _pmRepo.GetActiveByOrgAsync(orgId, ct);
            if (pm is null) return StatusCode(500, new { message = "Payment method not found after finalize." });

            // Opcional: cabeceras no-cache
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            return Ok(pm);
        }

        [HttpPost("subscription/hosted-link")]
        public async Task<IActionResult> GetHostedLink([FromBody] CheckoutRequest body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body.planCode))
                return BadRequest(new { message = "planCode requerido" });

            var (ok, orgId) = await TryGetOrgIdAsync(ct);
            if (!ok) return Unauthorized(new { message = "Auth requerida" });

            // Guard-rail (ya lo tienes): seats/downgrade, etc.
            var (can, reason) = await _orchestrator.CanChangeToPlanAsync(orgId, body.planCode, ct);
            if (!can) return Conflict(new { message = reason });

            var url = await _orchestrator.GetHostedSubscriptionUrlAsync(orgId, body.planCode, ct);
            return Ok(new { url });
        }

        // BillingController.cs (mismo controller)
        [AllowAnonymous]
        [HttpPost("webhooks/tilopay/subscribe")]
        public Task<IActionResult> Tilopay_Subscribe(CancellationToken ct)
            => HandleTiloPayWebhookKindAsync("subscribe", ct);

        [AllowAnonymous]
        [HttpPost("webhooks/tilopay/payment")]
        public Task<IActionResult> Tilopay_Payment(CancellationToken ct)
            => HandleTiloPayWebhookKindAsync("payment", ct);

        [AllowAnonymous]
        [HttpPost("webhooks/tilopay/rejected")]
        public Task<IActionResult> Tilopay_Rejected(CancellationToken ct)
            => HandleTiloPayWebhookKindAsync("rejected", ct);

        [AllowAnonymous]
        [HttpPost("webhooks/tilopay/unsubscribe")]
        public Task<IActionResult> Tilopay_Unsubscribe(CancellationToken ct)
            => HandleTiloPayWebhookKindAsync("unsubscribe", ct);

        [AllowAnonymous]
        [HttpPost("webhooks/tilopay/reactive")]
        public Task<IActionResult> Tilopay_Reactive(CancellationToken ct)
            => HandleTiloPayWebhookKindAsync("reactive", ct);

       
        private async Task<IActionResult> HandleTiloPayWebhookKindAsync(string kind, CancellationToken ct)
        {
            // Lee el cuerpo (no asumimos JSON siempre; Tilopay a veces devuelve form-url-encoded)
            Request.EnableBuffering();
            string raw;
            using (var r = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                raw = await r.ReadToEndAsync();
            Request.Body.Position = 0;

            // 1) Parse: JSON -> diccionario; si no hay claves, intenta form-url-encoded
            var kv = TryParseJsonToStringDict(raw);
            if (kv.Count == 0)
                kv = TryParseFormUrlEncoded(raw);

            // 2) Extrae campos de interés (tolerante)
            Guid? orgId = ExtractGuidFromAny(kv, "org", "org_id");
            string? planCode = ExtractStringFromAny(kv, "plan", "plan_code", "product", "modality_title");

            string? status = kind switch
            {
                "subscribe" => "active",
                "reactive" => "active",
                "unsubscribe" => "canceled",
                _ => null
            };

            // 3) Suscripción (alta/reactivación/baja)
            if (kind is "subscribe" or "reactive" or "unsubscribe")
            {
                if (orgId.HasValue && !string.IsNullOrWhiteSpace(planCode))
                {
                    var ps = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                    var pe = ps.AddMonths(1);

                    var ent = await _billingRepo.GetEntitlementsByPlanCodeAsync(planCode.ToLowerInvariant(), ct);
                    if (ent.Count > 0)
                    {
                        // Idempotencia (usa la que ya manejas en este controller)
                        var key = $"tilopay:{orgId}:{kind}:{planCode}:{ps:O}:{pe:O}";
                        await TryInsertIdempotencyAsync(key, "TiloPay", orgId.Value, kind, planCode, ps, pe, ct);

                        await _orchestrator.ApplySubscriptionAndEntitlementsAsync(
                            orgId.Value,
                            planCode.ToLowerInvariant(),
                            status ?? "active",
                            ps, pe,
                            ent,
                            ct
                        );
                    }
                }
            }

            // 4) Pago / Rechazo
            if (kind is "payment" or "rejected")
            {
                int? amountCents = ExtractDecimalCents(kv, "amount", "modality_amount");
                string currency = ExtractStringFromAny(kv, "currency", "currency_iso") ?? "USD";
                string providerPaymentId = ExtractStringFromAny(kv, "transaction", "tilopay_transaction", "payment_id", "id")
                                           ?? Guid.NewGuid().ToString("N");
                string? orderNumber = ExtractStringFromAny(kv, "order", "orderNumber", "reference");
                string statusPay = (kind == "payment") ? "captured" : "failed";
                string? errorCode = (statusPay == "failed")
                    ? (ExtractStringFromAny(kv, "error", "message", "code") ?? "rejected")
                    : null;

                if (orgId.HasValue && amountCents.HasValue)
                {
                    // Upsert del pago (NO pasamos raw aquí; lo anexamos como evento)
                    var paymentId = await _paymentsRepo.UpsertFromProviderAsync(
                        orgId.Value,
                        provider: "tilopay",
                        providerPaymentId: providerPaymentId,
                        orderNumber: orderNumber,
                        amountCents: amountCents.Value,
                        currencyIso: currency,
                        status: statusPay,
                        errorCode: errorCode,
                        idempotencyKey: providerPaymentId, // estable
                        ct: ct
                    );

                    // Evento crudo para forense (ya con paymentId y orgId)
                    await _paymentsRepo.AppendEventRawAsync(
                        paymentId: paymentId,
                        orgId: orgId.Value,
                        eventType: $"tilopay.{kind}",
                        rawPayloadJson: raw,
                        happenedAtUtc: DateTime.UtcNow,
                        ct: ct
                    );
                }
                else
                {
                    // Si no tenemos org o monto, al menos deja el rastro crudo sin asociar
                    await _paymentsRepo.AppendEventRawAsync(
                        paymentId: Guid.Empty, // o null si tu repo acepta nullables
                        orgId: Guid.Empty,     // o null si tu repo acepta nullables
                        eventType: $"tilopay.{kind}.unresolved",
                        rawPayloadJson: raw,
                        happenedAtUtc: DateTime.UtcNow,
                        ct: ct
                    );
                }
            }

            return Ok(new { ok = true });
        }

        // BillingController.cs (misma clase)
        private async Task TryInsertIdempotencyAsync(string key, string provider, Guid orgId, string kind, string? planCode,
                                                     DateTime? periodStartUtc, DateTime? periodEndUtc, CancellationToken ct)
        {
            const string selectSql = @"SELECT 1 FROM dbo.webhook_idempotency WHERE idem_key = @k;";
            const string insertSql = @"
INSERT INTO dbo.webhook_idempotency(idem_key, provider, org_id, kind, plan_code, period_start_utc, period_end_utc, created_at_utc)
VALUES(@k, @p, @org, @kind, @plan, @ps, @pe, SYSUTCDATETIME());";

            await using var con = new Microsoft.Data.SqlClient.SqlConnection(_cfg.GetConnectionString("Default")!);
            await con.OpenAsync(ct);

            bool exists;
            await using (var sel = new Microsoft.Data.SqlClient.SqlCommand(selectSql, con))
            {
                sel.Parameters.AddWithValue("@k", key);
                exists = (await sel.ExecuteScalarAsync(ct)) != null;
            }

            if (!exists)
            {
                await using var ins = new Microsoft.Data.SqlClient.SqlCommand(insertSql, con);
                ins.Parameters.AddWithValue("@k", key);
                ins.Parameters.AddWithValue("@p", provider);
                ins.Parameters.AddWithValue("@org", orgId);
                ins.Parameters.AddWithValue("@kind", kind);
                ins.Parameters.AddWithValue("@plan", (object?)planCode ?? DBNull.Value);
                ins.Parameters.AddWithValue("@ps", (object?)periodStartUtc ?? DBNull.Value);
                ins.Parameters.AddWithValue("@pe", (object?)periodEndUtc ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync(ct);
            }
        }



        public sealed class FinalizePaymentMethodRequest
        {
            public string ProviderPmId { get; set; } = default!; // token/id que usarás para cobrar
            public string? Brand { get; set; }
            public string? Last4 { get; set; }
            public int? ExpMonth { get; set; }
            public int? ExpYear { get; set; }
            public string? RawPayload { get; set; } // opcional: guarda todo lo que te devuelva el retorno
        }

        // ==== Helpers de parseo y extracción (webhook) ====

        // Normaliza JSON a diccionario string->string (si hay números/booleanos, los convierte a string)
        private static Dictionary<string, string> TryParseJsonToStringDict(string? body)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(body)) return dict;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    string val = p.Value.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => p.Value.GetString() ?? "",
                        System.Text.Json.JsonValueKind.Number => p.Value.ToString(),
                        System.Text.Json.JsonValueKind.True => "true",
                        System.Text.Json.JsonValueKind.False => "false",
                        _ => p.Value.ToString()
                    };
                    dict[p.Name] = val;
                }
            }
            catch
            {
                // best-effort
            }
            return dict;
        }

        private static Dictionary<string, string> TryParseFormUrlEncoded(string? body)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(body)) return dict;

            var pairs = body.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in pairs)
            {
                var kv = p.Split('=', 2);
                var k = Uri.UnescapeDataString(kv[0] ?? "");
                var v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1] ?? "") : "";
                dict[k] = v;
            }
            return dict;
        }

        // Overload: obtiene Guid? desde cualquier de las keys disponibles
        private static Guid? ExtractGuidFromAny(IDictionary<string, string> kv, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (kv.TryGetValue(k, out var s) && Guid.TryParse(s, out var g))
                    return g;
            }
            return null;
        }

        // Overload: obtiene string desde cualquier de las keys disponibles (omite vacíos)
        private static string? ExtractStringFromAny(IDictionary<string, string> kv, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (kv.TryGetValue(k, out var s) && !string.IsNullOrWhiteSpace(s))
                    return s;
            }
            return null;
        }

        // Overload: obtiene monto decimal en CENTAVOS desde cualquiera de las keys ("123.45" -> 12345)
        private static int? ExtractDecimalCents(IDictionary<string, string> kv, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (!kv.TryGetValue(k, out var s) || string.IsNullOrWhiteSpace(s)) continue;

                // "123.45" -> 12345
                if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dec))
                {
                    return (int)Math.Round(dec * 100m, 0, MidpointRounding.AwayFromZero);
                }
                // Si viene ya en centavos como entero
                if (int.TryParse(s, out var iv))
                {
                    return iv;
                }
            }
            return null;
        }


    }
}
