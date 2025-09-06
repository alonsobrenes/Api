// Services/Billing/FakeGateway.cs
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace EPApi.Services.Billing
{
    
    /// <summary>
    /// Pasarela "fake" para desarrollo/QA: no cobra nada.
    /// - CreateCheckoutSession: devuelve una URL simulada que podrías abrir en FE.
    /// - GetCustomerPortalUrl: URL simulada de portal del cliente.
    /// - ParseWebhook: acepta un JSON muy simple para probar el flujo end-to-end.
    /// </summary>
    public sealed class FakeGateway : IBillingGateway
    {
        private readonly Guid? _devOrgId;
        private readonly string _returnBase;

        private Guid ParseOrgId(string? raw)
        {
            var s = (raw ?? string.Empty).Trim();
            if (s.Length >= 2 && s.StartsWith("<") && s.EndsWith(">"))
            {
                s = s.Substring(1, s.Length - 2);
            }
            if (Guid.TryParse(s, out var g))
                return g;
            if (_devOrgId.HasValue)
                return _devOrgId.Value;
            // If still invalid, throw a clearer error:
            throw new FormatException($"Invalid orgId '{raw}'. Provide a valid GUID or set Billing:DevOrgId for FakeGateway.");
        }

        private static string BuildIdempotencyKey(BillingEventKind kind, System.Text.Json.JsonElement ev)
        {
            string plan = ev.TryGetProperty("planCode", out var pc) && pc.ValueKind == JsonValueKind.String ? (pc.GetString() ?? "").ToLowerInvariant() : "";
            string status = ev.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? (st.GetString() ?? "").ToLowerInvariant() : "";
            string ps = ev.TryGetProperty("periodStartUtc", out var p1) && p1.ValueKind == JsonValueKind.String ? p1.GetString() ?? "" : "";
            string pe = ev.TryGetProperty("periodEndUtc", out var p2) && p2.ValueKind == JsonValueKind.String ? p2.GetString() ?? "" : "";
            string orgRaw = ev.TryGetProperty("orgId", out var og) && og.ValueKind == JsonValueKind.String ? og.GetString() ?? "" : "";
            if (orgRaw.Length >= 2 && orgRaw.StartsWith("<") && orgRaw.EndsWith(">")) orgRaw = orgRaw.Substring(1, orgRaw.Length - 2);

            string k = $"fake:{orgRaw}:{kind}:{plan}:{status}:{ps}:{pe}";
            if (k.Length > 200) k = k.Substring(0, 200);
            return k;
        }


        public FakeGateway(IConfiguration cfg)
        {
            _returnBase = cfg["Billing:ReturnUrlBase"] ?? "http://localhost:5173";
            var dev = cfg["Billing:DevOrgId"] ?? cfg["Auth:DevOrgId"]; // fallback dev org for tests
            if (Guid.TryParse(dev, out var g)) _devOrgId = g;
        }

        public Task<string> CreateCheckoutSession(Guid orgId, string planCode, string returnUrl, CancellationToken ct)
        {
            // En una pasarela real redirigirías a Paddle/TiloPay.
            // Acá devolvemos una "URL de checkout" de mentira (podés reemplazar por una mini-página tuya).
            var url = $"{_returnBase}/checkout/fake?org={orgId}&plan={Uri.EscapeDataString(planCode)}&return={Uri.EscapeDataString(returnUrl)}";
            return Task.FromResult(url);
        }

        public Task<string> GetCustomerPortalUrl(Guid orgId, CancellationToken ct)
        {
            var url = $"{_returnBase}/account/billing/portal?org={orgId}";
            return Task.FromResult(url);
        }

        /// <summary>
        /// Espera un JSON con este shape (solo para pruebas):
        /// {
        ///   "events": [{
        ///      "kind": "SubscriptionActivated|SubscriptionUpdated|SubscriptionCanceled",
        ///      "orgId": "GUID",
        ///      "planCode": "solo|clinic|pro",
        ///      "status": "active|canceled|trialing",
        ///      "periodStartUtc": "2025-09-04T00:00:00Z",
        ///      "periodEndUtc": "2025-10-01T00:00:00Z"
        ///   }]
        /// }
        /// </summary>
        public async Task<IReadOnlyList<BillingEvent>> ParseWebhook(HttpRequest request, CancellationToken ct)
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            var list = new List<BillingEvent>();
            if (root.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in evs.EnumerateArray())
                {
                    var kindStr = e.GetProperty("kind").GetString() ?? "SubscriptionUpdated";
                    var kind = kindStr switch
                    {
                        "SubscriptionActivated" => BillingEventKind.SubscriptionActivated,
                        "SubscriptionCanceled" => BillingEventKind.SubscriptionCanceled,
                        _ => BillingEventKind.SubscriptionUpdated
                    };
                    list.Add(new BillingEvent
                    {
                        Kind = kind,
                        OrgId = ParseOrgId(e.GetProperty("orgId").GetString()),
                        PlanCode = e.GetProperty("planCode").GetString() ?? "",
                        Status = e.TryGetProperty("status", out var st) ? (st.GetString() ?? "active") : "active",
                        PeriodStartUtc = e.TryGetProperty("periodStartUtc", out var ps) && ps.ValueKind == JsonValueKind.String ? DateTime.Parse(ps.GetString()!) : (DateTime?)null,
                        PeriodEndUtc = e.TryGetProperty("periodEndUtc", out var pe) && pe.ValueKind == JsonValueKind.String ? DateTime.Parse(pe.GetString()!) : (DateTime?)null,
                        RawProviderPayload = e.ToString(),
                        Provider = "Fake",
                        IdempotencyKey = BuildIdempotencyKey(kind, e)
                    });
                }
            }
            return list;
        }
    }


}
