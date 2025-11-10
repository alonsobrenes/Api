// File: Controllers/BillingSimCheckoutController.cs
using System.Data;
using Microsoft.Data.SqlClient;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EPApi.Services.Orgs;

namespace EPApi.Controllers;

[ApiController]
[Authorize]
public sealed class BillingSimCheckoutController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly IOrgAccessService _orgAccess;
    private readonly string _cs;

    public BillingSimCheckoutController(IConfiguration cfg, IOrgAccessService orgAccess)
    {
        _cfg = cfg;
        _orgAccess = orgAccess;
        _cs = cfg.GetConnectionString("Default")!;
    }

    // --------- Helpers de contexto/seguridad ---------

    private bool TryGetOrgIdFromHeader(out Guid orgId)
    {
        orgId = default;
        if (!Request.Headers.TryGetValue("x-org-id", out var values)) return false;
        var s = values.FirstOrDefault();
        return Guid.TryParse(s, out orgId);
    }

    private int? GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? User.FindFirstValue(ClaimTypes.Name)
               ?? User.FindFirstValue("sub")
               ?? User.FindFirstValue("uid");
        return int.TryParse(sub, out var id) ? id : (int?)null;
    }

    private static bool IsOwnerRole(ClaimsPrincipal user)
    {
        var role = user.FindFirstValue(ClaimTypes.Role) ?? user.FindFirstValue("role");
        return string.Equals(role, "owner", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsAuthorizedOwnerAsync(Guid orgId, CancellationToken ct)
    {
        if (IsOwnerRole(User)) return true;
        var uid = GetCurrentUserId();
        if (uid == null) return false;
        return await _orgAccess.IsOwnerOfMultiSeatOrgAsync(uid.Value, orgId, ct);
    }

    private static string AppendQuery(string baseUrl, IDictionary<string, string?> query)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        foreach (var kv in query) q[kv.Key] = kv.Value;
        var ub = new UriBuilder(uri) { Query = q.ToString()! };
        return ub.ToString();
    }

    
    private bool TryGetOrgIdFromQueryOrHeader(out Guid orgId)
    {
        orgId = default;

        // 1) query ?org=...
        if (Request.Query.TryGetValue("org", out var qv))
        {
            var s = qv.FirstOrDefault();
            if (Guid.TryParse(s, out orgId)) return true;
        }

        // 2) header x-org-id
        if (Request.Headers.TryGetValue("x-org-id", out var hv))
        {
            var s = hv.FirstOrDefault();
            if (Guid.TryParse(s, out orgId)) return true;
        }

        return false;
    }


    // --------- Endpoints del hosted checkout simulado ---------

    // GET /api/billing/sim-checkout?planCode=clinic_3&returnUrl=http://localhost:5173/account/billing/return
    [HttpGet("/api/billing/sim-checkout")]
    [AllowAnonymous]
    public async Task<IActionResult> SimCheckout([FromQuery] string planCode, [FromQuery] string returnUrl, CancellationToken ct)
    {
        //if (!TryGetOrgIdFromHeader(out var orgId)) return BadRequest("Missing x-org-id");
        if (!TryGetOrgIdFromQueryOrHeader(out var orgId)) return BadRequest("Missing org (query or x-org-id)");
        //if (!await IsAuthorizedOwnerAsync(orgId, ct)) return Forbid();

        var html = $@"<!doctype html>
<html><head><meta charset='utf-8'><title>Sim Checkout</title>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<style>
  body {{ font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif; padding: 24px; }}
  .card {{ border:1px solid #ddd; border-radius:12px; padding:16px; max-width:560px; }}
  .row {{ margin:12px 0; }}
  .btn {{ display:inline-block; padding:10px 16px; border-radius:8px; text-decoration:none; }}
  .btn-primary {{ background:#2563eb; color:#fff; }}
  .btn-secondary {{ background:#e5e7eb; color:#111827; }}
</style></head>
<body>
  <div class='card'>
    <h2>Pago simulado</h2>
    <div class='row'>Plan seleccionado: <b>{System.Net.WebUtility.HtmlEncode(planCode)}</b></div>
    <div class='row'>Organización: <code>{orgId}</code></div>
    <div class='row' style='display:flex; gap:8px;'>
      <a class='btn btn-primary' href='/api/billing/sim-checkout/pay?planCode={Uri.EscapeDataString(planCode)}&returnUrl={Uri.EscapeDataString(returnUrl)}&org={orgId}'>Pagar</a>
      <a class='btn btn-secondary' href='/api/billing/sim-checkout/cancel?returnUrl={Uri.EscapeDataString(returnUrl)}'>Cancelar</a>
    </div>
  </div>
</body></html>";
        return Content(html, "text/html; charset=utf-8");
    }

    // GET /api/billing/sim-checkout/cancel?returnUrl=...
    [HttpGet("/api/billing/sim-checkout/cancel")]
    [AllowAnonymous]
    public IActionResult SimCancel([FromQuery] string returnUrl)
    {
        var url = AppendQuery(returnUrl, new Dictionary<string, string?>
        {
            ["status"] = "cancel"
        });
        return Redirect(url);
    }

    // GET /api/billing/sim-checkout/pay?planCode=...&returnUrl=...
    [HttpGet("/api/billing/sim-checkout/pay")]
    [AllowAnonymous]
    public async Task<IActionResult> SimPay([FromQuery] string planCode, [FromQuery] string returnUrl, CancellationToken ct)
    {
        //if (!TryGetOrgIdFromHeader(out var orgId)) return BadRequest("Missing x-org-id");
        if (!TryGetOrgIdFromQueryOrHeader(out var orgId)) return BadRequest("Missing org (query or x-org-id)");
        //if (!await IsAuthorizedOwnerAsync(orgId, ct)) return Forbid();

        // 1) Precio del plan (billing_plans.monthly_usd)
        var price = await GetPlanPriceAsync(planCode, ct);

        // 2) Activar/actualizar suscripción (periodo = ahora → +1 mes)
        var now = DateTime.UtcNow;
        var end = now.AddMonths(1);
        await UpsertSubscriptionAsync(
            orgId: orgId,
            planCode: planCode,
            provider: "tilopay-sim",
            status: "active",
            startUtc: now,
            endUtc: end,
            ct: ct
        );

        // 3) Insert de pago en TU esquema actual (payments + payment_events)
        var orderNumber = Guid.NewGuid().ToString("N"); // simulación
        var paymentId = await InsertPaymentAsync(
            orgId: orgId,
            providerPaymentId: null,
            orderNumber: orderNumber,
            amountCents: price.AmountCents,
            currencyIso: price.Currency ?? "USD",
            status: "captured",
            errorCode: null,
            idempotencyKey: orderNumber,
            ct: ct
        );

        await InsertPaymentEventAsync(
            paymentId: paymentId,
            orgId: orgId,
            eventType: "payment.captured",
            rawPayloadJson: $"{{\"planCode\":\"{planCode}\",\"amountCents\":{price.AmountCents},\"currency\":\"{price.Currency}\",\"sim\":true}}",
            happenedAtUtc: now,
            ct: ct
        );

        // 4) Redirigir al returnUrl con status=success
        var url = AppendQuery(returnUrl, new Dictionary<string, string?>
        {
            ["status"] = "success",
            ["planCode"] = planCode
        });
        return Redirect(url);
    }

    // --------- Persistencia (ADO.NET, alineado a tu DB) ---------

    private sealed record PlanPrice(int AmountCents, string Currency);

    private async Task<PlanPrice> GetPlanPriceAsync(string planCode, CancellationToken ct)
    {
        // Tu esquema: billing_plans (price_amount_cents INT, currency NVARCHAR/CHAR)
        const string sql = @"
SELECT TOP 1 price_amount_cents, currency
FROM dbo.billing_plans
WHERE code = @code AND is_active = 1";

        await using var con = new SqlConnection(_cs);
        await con.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@code", planCode);

        await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await rd.ReadAsync(ct))
            throw new InvalidOperationException($"Plan no encontrado o inactivo: {planCode}");

        var amountCents = rd.GetInt32(0);
        var currency = rd.IsDBNull(1) ? "USD" : rd.GetString(1);
        return new PlanPrice(amountCents, currency);
    }


    private async Task<decimal> GetMonthlyUsdAsync(string planCode, CancellationToken ct)
    {
        const string sql = @"SELECT TOP 1 monthly_usd FROM dbo.billing_plans WHERE code = @code";
        await using var con = new SqlConnection(_cs);
        await con.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@code", planCode);
        var obj = await cmd.ExecuteScalarAsync(ct);
        if (obj == null || obj is DBNull) return 0m;
        return Convert.ToDecimal(obj);
    }

    private async Task UpsertSubscriptionAsync(Guid orgId, string planCode, string provider, string status,
        DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        // Ajusta nombres si tu tabla difiere, pero mantenemos las columnas que nos compartiste
        const string updateSql = @"
UPDATE dbo.subscriptions
SET
  plan_code = @plan_code,
  provider = @provider,
  status = @status,
  current_period_start_utc = @start,
  current_period_end_utc = @end
WHERE org_id = @org_id;";

        const string insertSql = @"
INSERT INTO dbo.subscriptions(org_id, plan_code, provider, status, current_period_start_utc, current_period_end_utc)
VALUES(@org_id, @plan_code, @provider, @status, @start, @end);";

        await using var con = new SqlConnection(_cs);
        await con.OpenAsync(ct);
        await using var tx = await con.BeginTransactionAsync(ct) as SqlTransaction;

        try
        {
            var cmd = new SqlCommand(updateSql, con, tx);
            cmd.Parameters.AddWithValue("@org_id", orgId);
            cmd.Parameters.AddWithValue("@plan_code", planCode);
            cmd.Parameters.AddWithValue("@provider", provider);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@start", startUtc);
            cmd.Parameters.AddWithValue("@end", endUtc);
            var rows = await cmd.ExecuteNonQueryAsync(ct);

            if (rows == 0)
            {
                var cmd2 = new SqlCommand(insertSql, con, tx);
                cmd2.Parameters.AddWithValue("@org_id", orgId);
                cmd2.Parameters.AddWithValue("@plan_code", planCode);
                cmd2.Parameters.AddWithValue("@provider", provider);
                cmd2.Parameters.AddWithValue("@status", status);
                cmd2.Parameters.AddWithValue("@start", startUtc);
                cmd2.Parameters.AddWithValue("@end", endUtc);
                await cmd2.ExecuteNonQueryAsync(ct);
            }

            await tx!.CommitAsync(ct);
        }
        catch
        {
            await tx!.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<Guid> InsertPaymentAsync(Guid orgId, string? providerPaymentId, string orderNumber,
        int amountCents, string currencyIso, string status, string? errorCode, string? idempotencyKey, CancellationToken ct)
    {
        // Insertamos con 'id' explícito para poder referenciarlo en payment_events
        const string sql = @"
INSERT INTO dbo.payments(
  id, org_id, provider, provider_payment_id, order_number,
  amount_cents, currency_iso, status, error_code, idempotency_key
) VALUES (
  @id, @org_id, 'tilopay', @tpid, @ord, @amount, @cur, @status, @err, @idem
);";

        var id = Guid.NewGuid();

        await using var con = new SqlConnection(_cs);
        await con.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@org_id", orgId);
        cmd.Parameters.AddWithValue("@tpid", (object?)providerPaymentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ord", (object?)orderNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@amount", amountCents);
        cmd.Parameters.AddWithValue("@cur", currencyIso);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@err", (object?)errorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@idem", (object?)idempotencyKey ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        return id;
    }

    private async Task InsertPaymentEventAsync(Guid paymentId, Guid orgId, string eventType, string rawPayloadJson, DateTime happenedAtUtc, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.payment_events(payment_id, org_id, event_type, raw_payload, happened_at_utc)
VALUES (@pid, @org, @evt, @raw, @when);";

        await using var con = new SqlConnection(_cs);
        await con.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@pid", paymentId);
        cmd.Parameters.AddWithValue("@org", orgId);
        cmd.Parameters.AddWithValue("@evt", eventType);
        cmd.Parameters.AddWithValue("@raw", rawPayloadJson);
        cmd.Parameters.AddWithValue("@when", happenedAtUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
