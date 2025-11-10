using EPApi.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EPApi.Services.Billing
{
    /// <summary>
    /// Gateway real contra TiloPay.
    /// Principios:
    ///  - Usa HttpClientFactory con dos clientes:
    ///      * "TiloPay.SafeClient"     -> con retry corto (login, consult, tokenize)
    ///      * "TiloPay.PaymentClient"  -> sin retry global (processPayment/modify)
    ///  - No hace lógica de negocio: entrega eventos a BillingOrchestrator a través del Controller.
    ///  - No reintenta cobros automáticamente; para reintentos, primero se debe CONSULTAR por orderNumber.
    /// </summary>
    public sealed class TiloPayGateway : IBillingGateway
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _cfg;
        private readonly ILogger<TiloPayGateway> _log;
        private readonly IPaymentMethodTokenizeContextProvider _pmCtxProvider;
        private readonly ITiloPayAuthTokenProvider _tokenProvider;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        public TiloPayGateway(
            IHttpClientFactory httpFactory,
            IConfiguration cfg,
            ILogger<TiloPayGateway> log,
            IPaymentMethodTokenizeContextProvider pmCtxProvider,
            ITiloPayAuthTokenProvider tokenProvider)
        {
            _httpFactory = httpFactory;
            _cfg = cfg;
            _log = log;
            _pmCtxProvider = pmCtxProvider;
            _tokenProvider = tokenProvider;
        }

        /// <summary>
        /// Genera una sesión de checkout (hosted) con TiloPay (processPayment -> type=100 url),
        /// devolviendo la URL a la que debe redirigirse el usuario.
        /// </summary>
        public async Task<string> CreateCheckoutSession(Guid orgId, string planCode, string returnUrl, CancellationToken ct)
        {
            var token = await _tokenProvider.GetBearerAsync(ct);
            var baseUrl = GetBaseUrl();
            var apiKey = GetApiKey();
            var platform = _cfg["TiloPay:Platform"] ?? "AlphaDocApi";
            
            var (amountCents, currency, description) = await ResolvePlanPricingAsync(planCode, ct);

            // orderNumber ÚNICO y ESTABLE por intento (sirve para correlacionar y evitar duplicados si alguna vez se reintenta)
            var orderNumber = BuildOrderNumber(orgId, planCode);

            // processPayment → devuelve URL (type=100). NO aplicamos retry global.
            var client = _httpFactory.CreateClient("TiloPay.PaymentClient");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var body = new Dictionary<string, object?>
            {
                ["key"] = apiKey,
                ["amount"] = (amountCents / 100.0m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                ["currency"] = currency,         // "CRC" | "USD"
                ["orderNumber"] = orderNumber,
                ["description"] = description,
                ["redirect"] = returnUrl,
                ["capture"] = true,
                ["subscription"] = true,            // Si migramos a plan recurrente nativo, esto puede variar
                ["platform"] = platform
            };

            // TODO(billing-address): cuando confirmemos org_profiles/org_addresses, completar billTo*/shipTo*:
            // body["billToName"] = ...; body["billToEmail"] = ...; body["billToPhone"] = ...;
            // body["billToAddress"] = new { line1=..., line2=..., city=..., state_region=..., postal_code=..., country_iso2=... };
            // body["shipTo*"] = ...

            var reqJson = JsonSerializer.Serialize(body, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/processPayment")
            {
                Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
            };

            using var res = await client.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogError("TiloPay processPayment error {Status} {Body}", res.StatusCode, json);
                throw new InvalidOperationException("Error al iniciar checkout en TiloPay");
            }

            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null; // "100"
            if (!string.Equals(type, "100", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Respuesta TiloPay inesperada (type={type})");

            var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("URL de checkout vacía");

            return url!;
        }

        /// <summary>
        /// Devuelve una URL para que el cliente actualice su método de pago (tokenize).
        /// TiloPay no tiene "customer portal" canónico como Stripe; MVP: processTokenize.
        /// </summary>
        public async Task<string> GetCustomerPortalUrl(Guid orgId, CancellationToken ct)
        {
            var token = await _tokenProvider.GetBearerAsync(ct);
            var baseUrl = GetBaseUrl();
            var apiKey = GetApiKey();
            var thanks = _cfg["TiloPay:ThanksUrl"] ?? GetReturnUrlBase(); // fallback razonable
            var path = _cfg["TiloPay:Endpoints:ProcessTokenize"] ?? "/api/v1/processTokenize";
            var client = _httpFactory.CreateClient("TiloPay.SafeClient");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var body = new
            {
                key = apiKey,
                thanks_url = thanks
            };

            var reqJson = JsonSerializer.Serialize(body, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{path}")
            {
                Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
            };

            using var res = await client.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogError("TiloPay processTokenize error {Status} {Body}", res.StatusCode, json);
                throw new InvalidOperationException("Error al generar URL de tokenización en TiloPay");
            }

            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null; // "100"
            var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (!string.Equals(type, "100", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("Respuesta TiloPay inesperada al tokenizar");

            return url!;
        }

        /// <summary>
        /// Parsea el webhook de TiloPay y lo normaliza a nuestra lista de BillingEvent.
        /// La verificación de firma/HMAC se integra con Billing.Webhook.* (header/algoritmo/timestamp)
        /// </summary>
        public async Task<IReadOnlyList<BillingEvent>> ParseWebhook(HttpRequest request, CancellationToken ct)
        {
            // === Leer body crudo (necesario para verificación de firma) ===
            string raw;
            using (var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true))
            {
                raw = await reader.ReadToEndAsync();
            }
            request.Body.Position = 0;

            // === Verificación de firma (si la doc lo soporta) ===
            // Config alineada con tu appsettings: Billing.Webhook.*
            var verify = _cfg.GetValue<bool?>("Billing:Webhook:Verify") ?? false;
            if (verify)
            {
                var headerName = _cfg["Billing:Webhook:Header"] ?? "X-Signature";
                var algo = _cfg["Billing:Webhook:Algorithm"] ?? "HMACSHA256";
                var tsHeader = _cfg["Billing:Webhook:TimestampHeader"];
                var skewSec = _cfg.GetValue<int?>("Billing:Webhook:SkewSeconds") ?? 300;
                var allowBodyOnly = _cfg.GetValue<bool?>("Billing:Webhook:AllowBodyOnly") ?? false;
                var secret = _cfg["Billing:Webhook:Secret"]; // TODO: setear cuando TiloPay defina el secreto

                var sigHeader = request.Headers[headerName].FirstOrDefault();
                var tsHeaderVal = string.IsNullOrWhiteSpace(tsHeader) ? null : request.Headers[tsHeader].FirstOrDefault();

                var ok = VerifyWebhookSignature(raw, sigHeader, tsHeaderVal, secret, algo, skewSec, allowBodyOnly);
                if (!ok)
                {
                    _log.LogWarning("Firma de webhook TiloPay inválida");
                    throw new UnauthorizedAccessException("Webhook signature invalid");
                }
            }

            // === Parseo y normalización ===
            var events = new List<BillingEvent>();
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                // NOTA: El payload exacto depende de cuál endpoint originó el evento (plan, pago, link).
                // En la primera corrida sandbox guardaremos todo en payment_events
                // y ajustaremos el mapeo exacto. Aquí dejamos la estructura.

                var evt = root.TryGetProperty("event", out var evProp) ? evProp.GetString() : null;
                var now = DateTime.UtcNow;

                if (string.Equals(evt, "subscription.activated", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(new BillingEvent
                    {
                        Kind = BillingEventKind.SubscriptionActivated,
                        OrgId = ExtractOrgId(root) ?? Guid.Empty,
                        PlanCode = ExtractPlanCode(root) ?? "unknown",
                        Status = ExtractStatus(root) ?? "active",
                        PeriodStartUtc = ExtractDate(root, "periodStart"),
                        PeriodEndUtc = ExtractDate(root, "periodEnd"),
                        RawProviderPayload = raw,
                        Provider = "TiloPay",
                        IdempotencyKey = ExtractEventId(root) ?? BuildIdemFromPayload(raw, now)
                    });
                }
                else if (string.Equals(evt, "subscription.updated", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(new BillingEvent
                    {
                        Kind = BillingEventKind.SubscriptionUpdated,
                        OrgId = ExtractOrgId(root) ?? Guid.Empty,
                        PlanCode = ExtractPlanCode(root) ?? "unknown",
                        Status = ExtractStatus(root) ?? "active",
                        PeriodStartUtc = ExtractDate(root, "periodStart"),
                        PeriodEndUtc = ExtractDate(root, "periodEnd"),
                        RawProviderPayload = raw,
                        Provider = "TiloPay",
                        IdempotencyKey = ExtractEventId(root) ?? BuildIdemFromPayload(raw, now)
                    });
                }
                else if (string.Equals(evt, "subscription.canceled", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(evt, "subscription.cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(new BillingEvent
                    {
                        Kind = BillingEventKind.SubscriptionCanceled,
                        OrgId = ExtractOrgId(root) ?? Guid.Empty,
                        PlanCode = ExtractPlanCode(root) ?? "unknown",
                        Status = "canceled",
                        RawProviderPayload = raw,
                        Provider = "TiloPay",
                        IdempotencyKey = ExtractEventId(root) ?? BuildIdemFromPayload(raw, now)
                    });
                }
                else
                {
                    // Otros: pagos one-off capturados, refunds, etc. → los registrará BillingController (payment_events)
                    _log.LogInformation("Webhook TiloPay ignorado (MVP): {Event}", evt ?? "(sin event)");
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "No se pudo parsear webhook TiloPay");
                // Devolver vacío permite al controller responder 200 idempotente si corresponde
            }

            return events;
        }

        public async Task<string> CreateTokenizationSessionAsync(Guid orgId, string returnUrl, CancellationToken ct)
        {            
            var token = await _tokenProvider.GetBearerAsync(ct);
            var baseUrl = GetBaseUrl();
            var apiKey = GetApiKey();
            var platform = _cfg["TiloPay:Platform"] ?? "AlphaDocApi";
            var ctx = await _pmCtxProvider.GetContextAsync(orgId, ct);

            var client = _httpFactory.CreateClient("TiloPay.PaymentClient");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var body = new Dictionary<string, object?>
            {
                ["redirect"] = returnUrl,          
                ["key"] = apiKey,              
                ["email"] = ctx.Email,           
                ["language"] = ctx.Language,        
                ["firstname"] = ctx.FirstName,       
                ["lastname"] = ctx.LastName         
            };

            var reqJson = JsonSerializer.Serialize(body, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/processTokenize")
            {
                Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
            };

            using var res = await client.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                _log.LogError("TiloPay processTokenize error {Status} {Body}", res.StatusCode, json);
                throw new InvalidOperationException("Error al iniciar tokenización en TiloPay");
            }

            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null; // "100"
            if (!string.Equals(type, "100", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Respuesta TiloPay inesperada (type={type})");

            var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("URL de tokenización vacía");

            return url!;
        }

        public async Task<PaymentMethodDetails?> TryFetchPaymentMethodDetailsAsync(string providerPmId, CancellationToken ct)
        {
            try
            {
                var token = await _tokenProvider.GetBearerAsync(ct);
                var baseUrl = GetBaseUrl();
                var apiKey = GetApiKey();

                var client = _httpFactory.CreateClient("TiloPay.PaymentClient");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                // Ejemplo de contrato:
                // POST /api/v1/getTokenInfo { key, token }
                var body = new Dictionary<string, object?>
                {
                    ["key"] = apiKey,
                    ["token"] = providerPmId
                };

                var reqJson = JsonSerializer.Serialize(body, JsonOpts);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/getTokenInfo")
                {
                    Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
                };

                using var res = await client.SendAsync(req, ct);
                var json = await res.Content.ReadAsStringAsync(ct);

                if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _log.LogWarning("TiloPay getTokenInfo 404 para token {Token}", providerPmId);
                    return null;
                }

                if (!res.IsSuccessStatusCode)
                {
                    _log.LogWarning("TiloPay getTokenInfo error {Status} {Body}", res.StatusCode, json);
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Mapea según doc real (ajusta nombres si difieren)
                var brand = root.TryGetProperty("brand", out var b) ? b.GetString() : null;
                var last4 = root.TryGetProperty("last4", out var l4) ? l4.GetString() : null;
                int? expMonth = null, expYear = null;
                if (root.TryGetProperty("expMonth", out var em) && em.TryGetInt32(out var emv)) expMonth = emv;
                if (root.TryGetProperty("expYear", out var ey) && ey.TryGetInt32(out var eyv)) expYear = eyv;

                return new PaymentMethodDetails
                {
                    Brand = brand,
                    Last4 = last4,
                    ExpMonth = expMonth,
                    ExpYear = expYear,
                    RawProviderPayload = json
                };
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "No se pudo obtener detalles de token TiloPay");
                return null;
            }
        }

        public async Task<string> ListPlansRawAsync(CancellationToken ct)
        {
            var token = await _tokenProvider.GetBearerAsync(ct);
            var client = _httpFactory.CreateClient("TiloPay.PaymentClient");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var baseUrl = GetBaseUrl();
            var path = _cfg["TiloPay:Endpoints:ListPlans"] ?? throw new InvalidOperationException("TiloPay:Endpoints:ListPlans no configurado");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}{path}");
            var res = await client.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogError("TiloPay ListPlans error {Status} {Body}", res.StatusCode, json);
                throw new InvalidOperationException("No se pudo obtener el catálogo de planes/precios de TiloPay");
            }
            return json; // devolvemos "raw" para no asumir esquema
        }

        public async Task<string> CreatePlanRawAsync(object body, CancellationToken ct)
        {
            var token = await _tokenProvider.GetBearerAsync(ct);
            var client = _httpFactory.CreateClient("TiloPay.PaymentClient");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var baseUrl = GetBaseUrl();
            var path = _cfg["TiloPay:Endpoints:CreatePlan"] ?? throw new InvalidOperationException("TiloPay:Endpoints:CreatePlan no configurado");
            var reqJson = JsonSerializer.Serialize(body, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{path}")
            { Content = new StringContent(reqJson, Encoding.UTF8, "application/json") };

            var res = await client.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogError("TiloPay CreatePlan error {Status} {Body}", res.StatusCode, json);
                throw new InvalidOperationException("No se pudo crear el plan en TiloPay");
            }
            return json; // raw
        }

        public async Task<string> UpdatePlanRawAsync(object body, CancellationToken ct)
        {
            var token = await _tokenProvider.GetBearerAsync(ct);
            var client = _httpFactory.CreateClient("TiloPay.PaymentClient");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var baseUrl = GetBaseUrl();
            var path = _cfg["TiloPay:Endpoints:UpdatePlan"] ?? throw new InvalidOperationException("TiloPay:Endpoints:UpdatePlan no configurado");
            var reqJson = JsonSerializer.Serialize(body, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{path}")
            { Content = new StringContent(reqJson, Encoding.UTF8, "application/json") };

            var res = await client.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogError("TiloPay UpdatePlan error {Status} {Body}", res.StatusCode, json);
                throw new InvalidOperationException("No se pudo actualizar el plan en TiloPay");
            }
            return json; // raw
        }

        /// <summary>
        /// Crea un "plan repeat" en TiloPay según tu fila local de billing_plans.
        /// Retorna el ID (string) que TiloPay asigna (ej: "4228").
        /// </summary>
        public async Task<string> CreateRepeatPlanAsync(BillingPlanDto plan, CancellationToken ct)
        {
            var token = await _tokenProvider.GetBearerAsync(ct);
            var client = _httpFactory.CreateClient("TiloPay.PaymentClient");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var baseUrl = GetBaseUrl();
            var path = _cfg["TiloPay:Endpoints:CreatePlanRepeat"] ?? "/api/v1/createPlanRepeat";
            var apiKey = _cfg["TiloPay:ApiKey"] ?? throw new InvalidOperationException("TiloPay:ApiKey no configurado");

            var frecuency = 1;
            var trial = plan.TrialDays > 0 ? 1 : 0;
            var trialDays = plan.TrialDays > 0 ? plan.TrialDays : 0;            
            var amountDecimal = plan.PriceAmountCents / 100.0m;

            // Webhooks y thanks url (pueden ser vacíos si aún no listos)
            var thanksUrl = _cfg["TiloPay:ThanksUrl"] ?? "";
            var wh = _cfg.GetSection("TiloPay:Webhooks");
            var whSubscribe = wh["Subscribe"] ?? "";
            var whPayment = wh["Payment"] ?? "";
            var whRejected = wh["Rejected"] ?? "";
            var whUnsub = wh["Unsubscribe"] ?? "";
            var whReactive = wh["Reactive"] ?? "";

            // Payload REAL basado en tu ejemplo exitoso de Postman
            var body = new Dictionary<string, object?>
            {
                ["key"] = apiKey,
                ["title"] = plan.Name,
                ["description"] = plan.Description ?? plan.Name,
                ["frecuency"] = frecuency,
                ["currency"] = plan.Currency,           // "USD" o "CRC"
                ["first_amount"] = 0,
                ["trial"] = trial,
                ["trial_days"] = trialDays,
                ["attempts"] = 1,
                ["modality"] = new[] {
                new Dictionary<string, object?>
                {
                    ["title"] = "Base",
                    ["amount"] = amountDecimal
                }
            },
                ["thanks_url"] = thanksUrl,
                ["webhook_subscribe"] = whSubscribe,
                ["webhook_payment"] = whPayment,
                ["webhook_rejected"] = whRejected,
                ["webhook_unsubscribe"] = whUnsub,
                ["webhook_reactive"] = whReactive,
                // ["end_at"] = "25-10-2023",  // opcional; si no tienes fecha, NO lo envíes
                ["notify"] = 0,
                ["notify_detail_es"] = "",
                ["notify_detail_en"] = "",
                ["notify_note_es"] = "",
                ["notify_note_en"] = ""
            };

            var reqJson = JsonSerializer.Serialize(body, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{path}")
            {
                Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
            };

            var res = await client.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                _log.LogError("TiloPay createPlanRepeat error {Status} {Body}", res.StatusCode, json);
                throw new InvalidOperationException("No se pudo crear el plan en TiloPay");
            }

            // Respuesta esperada:
            // {
            //   "type": "200",
            //   "status": 1,
            //   "message": "Success",
            //   "id": 4228,
            //   "url": "https://tp.cr/l/..."
            // }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var ty) ? ty.GetString() : null;
            var status = root.TryGetProperty("status", out var st) ? st.GetInt32() : 0;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;

            if (!string.Equals(type, "200", StringComparison.OrdinalIgnoreCase) || status != 1 || string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException($"Respuesta inesperada de TiloPay createPlanRepeat: {json}");

            return id!; // lo guardaremos en provider_price_id
        }

        public async Task<(string? registerUrl, string? renewUrl, string rawJson)>
        GetRecurrentUrlAsync(string providerPlanId, string email, CancellationToken ct)
        {
            var token = await _tokenProvider.GetBearerAsync(ct);
            var client = _httpFactory.CreateClient("TiloPay.PaymentClient");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var baseUrl = GetBaseUrl();
            var path = _cfg["TiloPay:Endpoints:RecurrentUrl"] ?? "/api/v1/recurrentUrl";
            var apiKey = _cfg["TiloPay:ApiKey"] ?? throw new InvalidOperationException("TiloPay:ApiKey no configurado");

            var body = new Dictionary<string, object?>
            {
                ["key"] = apiKey,
                ["id"] = providerPlanId,
                ["email"] = email
            };

            var reqJson = JsonSerializer.Serialize(body, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{path}")
            {
                Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
            };

            var res = await client.SendAsync(req, ct);
            var json = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                _log.LogError("TiloPay recurrentUrl error {Status} {Body}", res.StatusCode, json);
                throw new InvalidOperationException("No se pudo obtener el URL de registro/renovación recurrente en TiloPay");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Respuesta esperada:
            // { "type":"200", "message":"", "url_register":"https://...", "url_renew":"" }
            var register = root.TryGetProperty("url_register", out var r) ? r.GetString() : null;
            var renew = root.TryGetProperty("url_renew", out var rn) ? rn.GetString() : null;

            return (register, renew, json);
        }

        // ================= Helpers privados =================

        private static string BuildOrderNumber(Guid orgId, string planCode)
            => $"{planCode}-{orgId.ToString()[..8]}-{DateTime.UtcNow:yyyyMMddHHmmss}";

        // Cuando tengamos el origen real de precios (tabla/servicio), conectamos aquí:
        private static Task<(int amountCents, string currency, string description)> ResolvePlanPricingAsync(string planCode, CancellationToken ct)
        {
            // EJEMPLO: "solo" → 1999 USD
            return Task.FromResult((amountCents: 1999, currency: "USD", description: $"Plan {planCode}"));
        }

        // ===== Extractores dependientes del payload real de TiloPay (ajustar tras primera corrida) =====

        private static Guid? ExtractOrgId(JsonElement root)
        {
            // Opción A: viene como campo directo "orgId"
            if (root.TryGetProperty("orgId", out var o) && Guid.TryParse(o.GetString(), out var g)) return g;

            // Opción B: viene en orderNumber "PLAN-xxxxxxxx-YYYYMMDDHHmmss"
            var ord = ExtractString(root, "orderNumber") ?? ExtractString(root, "reference");
            if (!string.IsNullOrWhiteSpace(ord))
            {
                var parts = ord.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1].Length == 8)
                {
                    // no podemos reconstruir el GUID con 8 chars; necesitaríamos otra pista.
                    // En la primera corrida, guardamos raw y decidimos estrategia (metadata, etc.)
                }
            }
            return null;
        }

        private static string? ExtractPlanCode(JsonElement root)
            => ExtractString(root, "planCode") ?? ExtractString(root, "plan") ?? ExtractString(root, "product");

        private static string? ExtractStatus(JsonElement root)
            => ExtractString(root, "status");

        private static DateTime? ExtractDate(JsonElement root, string prop)
        {
            var s = ExtractString(root, prop);
            if (DateTime.TryParse(s, out var dt)) return dt;
            return null;
        }

        private static string? ExtractEventId(JsonElement root)
            => ExtractString(root, "id") ?? ExtractString(root, "event_id") ?? ExtractString(root, "uuid");

        private static string? ExtractString(JsonElement root, string prop)
            => root.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        private static string BuildIdemFromPayload(string raw, DateTime utcNow)
            => $"{utcNow:yyyyMMddTHHmmssffff}-{raw.Length}";

        // ====== Config helpers ======

        private string GetBaseUrl()
        {
            var v = _cfg["TiloPay:BaseUrl"];
            if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException("TiloPay:BaseUrl requerido");
            return v.TrimEnd('/');
        }
        private string GetApiUser()
        {
            var v = _cfg["TiloPay:ApiUser"];
            if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException("TiloPay:ApiUser requerido");
            return v;
        }
        private string GetApiPass()
        {
            var v = _cfg["TiloPay:ApiPass"];
            if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException("TiloPay:ApiPass requerido");
            return v;
        }
        private string GetApiKey()
        {
            var v = _cfg["TiloPay:ApiKey"];
            if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException("TiloPay:ApiKey requerido");
            return v;
        }
        private string GetReturnUrlBase()
        {
            var v = _cfg["Billing:ReturnUrlBase"];
            if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException("Billing:ReturnUrlBase requerido");
            return v;
        }

        // ====== Verificación de firma (placeholder genérico) ======
        private static bool VerifyWebhookSignature(
            string rawBody,
            string? signatureHeader,
            string? timestampHeader,
            string? secret,
            string algorithm,
            int skewSeconds,
            bool allowBodyOnly)
        {
            // Si no hay secreto configurado, no se puede verificar
            if (string.IsNullOrWhiteSpace(secret)) return true;

            // Placeholder: a completar cuando TiloPay confirme el método de firma
            // Patrón típico:
            //  - construir mensaje: timestamp + "." + rawBody (si usan ts)
            //  - HMACSHA256(secret, message)
            //  - comparar con signatureHeader (constant time)
            //  - validar skew entre timestampHeader y DateTime.UtcNow

            return true;
        }


    }
}
