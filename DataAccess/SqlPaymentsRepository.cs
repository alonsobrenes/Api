// DataAccess/SqlPaymentsRepository.cs

using EPApi.Models;
using Microsoft.Data.SqlClient;
using EPApi.DataAccess;

namespace EPApi.DataAccess
{   
    public sealed class SqlPaymentsRepository : IPaymentsRepository
    {
        private readonly string _cs;
        public SqlPaymentsRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")!;
        }

        public async Task<IReadOnlyList<PaymentListItem>> ListByOrgAsync(Guid orgId, int limit, CancellationToken ct)
        {
            const string sql = @"
SELECT TOP (@limit)
    p.id,
    p.org_id,
    p.provider,
    p.provider_payment_id,
    p.order_number,
    p.amount_cents,
    p.currency_iso,
    p.status,
    p.error_code,
    p.created_at_utc,
    p.updated_at_utc
FROM dbo.payments p
WHERE p.org_id = @orgId
ORDER BY p.created_at_utc DESC;";

            var list = new List<PaymentListItem>();
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddWithValue("@orgId", orgId);
            cmd.Parameters.AddWithValue("@limit", limit);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new PaymentListItem
                {
                    Id = rd.GetGuid(0),
                    OrgId = rd.GetGuid(1),
                    Provider = rd.GetString(2),
                    ProviderPaymentId = rd.IsDBNull(3) ? null : rd.GetString(3),
                    OrderNumber = rd.IsDBNull(4) ? null : rd.GetString(4),
                    AmountCents = rd.GetInt32(5),
                    CurrencyIso = rd.GetString(6),
                    Status = rd.GetString(7),
                    ErrorCode = rd.IsDBNull(8) ? null : rd.GetString(8),
                    CreatedAtUtc = rd.GetDateTime(9),
                    UpdatedAtUtc = rd.GetDateTime(10),
                });
            }
            return list;
        }

        public async Task<Guid> InsertAsync(Guid orgId,
                                            string provider,
                                            string? providerPaymentId,
                                            string? orderNumber,
                                            int amountCents,
                                            string currencyIso,
                                            string status,
                                            string? errorCode,
                                            string? idempotencyKey,
                                            CancellationToken ct)
        {
            const string sql = @"
INSERT INTO dbo.payments(
  id, org_id, provider, provider_payment_id, order_number,
  amount_cents, currency_iso, status, error_code, idempotency_key
)
VALUES (
  @id, @org_id, @provider, @ppid, @ord,
  @amount, @currency, @status, @err, @idem
);";

            var id = Guid.NewGuid();

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@org_id", orgId);
            cmd.Parameters.AddWithValue("@provider", provider);
            cmd.Parameters.AddWithValue("@ppid", (object?)providerPaymentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ord", (object?)orderNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@amount", amountCents);
            cmd.Parameters.AddWithValue("@currency", currencyIso);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@err", (object?)errorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@idem", (object?)idempotencyKey ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);

            return id;
        }

        public async Task<Guid> UpsertFromProviderAsync(Guid orgId,
                                                        string provider,
                                                        string? providerPaymentId,
                                                        string? orderNumber,
                                                        int amountCents,
                                                        string currencyIso,
                                                        string status,
                                                        string? errorCode,
                                                        string? idempotencyKey,
                                                        CancellationToken ct)
        {
            // Estrategia: buscamos por provider_payment_id; si no hay, buscamos por order_number; si tampoco, insertamos.
            const string selectSql = @"
SELECT TOP 1 id
FROM dbo.payments
WHERE (provider = @provider)
  AND (
        (@ppid IS NOT NULL AND provider_payment_id = @ppid)
     OR (@ppid IS NULL AND @ord IS NOT NULL AND order_number = @ord)
  );";

            const string updateSql = @"
UPDATE dbo.payments
SET amount_cents = @amount,
    currency_iso = @currency,
    status = @status,
    error_code = @err,
    idempotency_key = @idem
WHERE id = @id;";

            const string insertSql = @"
INSERT INTO dbo.payments(
  id, org_id, provider, provider_payment_id, order_number,
  amount_cents, currency_iso, status, error_code, idempotency_key
)
VALUES (
  @id, @org_id, @provider, @ppid, @ord,
  @amount, @currency, @status, @err, @idem
);";

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);

            Guid? existingId = null;
            await using (var sel = new SqlCommand(selectSql, con))
            {
                sel.Parameters.AddWithValue("@provider", provider);
                sel.Parameters.AddWithValue("@ppid", (object?)providerPaymentId ?? DBNull.Value);
                sel.Parameters.AddWithValue("@ord", (object?)orderNumber ?? DBNull.Value);

                var obj = await sel.ExecuteScalarAsync(ct);
                if (obj != null && obj != DBNull.Value) existingId = (Guid)obj;
            }

            if (existingId.HasValue)
            {
                await using var upd = new SqlCommand(updateSql, con);
                upd.Parameters.AddWithValue("@id", existingId.Value);
                upd.Parameters.AddWithValue("@amount", amountCents);
                upd.Parameters.AddWithValue("@currency", currencyIso);
                upd.Parameters.AddWithValue("@status", status);
                upd.Parameters.AddWithValue("@err", (object?)errorCode ?? DBNull.Value);
                upd.Parameters.AddWithValue("@idem", (object?)idempotencyKey ?? DBNull.Value);
                await upd.ExecuteNonQueryAsync(ct);
                return existingId.Value;
            }
            else
            {
                var newId = Guid.NewGuid();
                await using var ins = new SqlCommand(insertSql, con);
                ins.Parameters.AddWithValue("@id", newId);
                ins.Parameters.AddWithValue("@org_id", orgId);
                ins.Parameters.AddWithValue("@provider", provider);
                ins.Parameters.AddWithValue("@ppid", (object?)providerPaymentId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@ord", (object?)orderNumber ?? DBNull.Value);
                ins.Parameters.AddWithValue("@amount", amountCents);
                ins.Parameters.AddWithValue("@currency", currencyIso);
                ins.Parameters.AddWithValue("@status", status);
                ins.Parameters.AddWithValue("@err", (object?)errorCode ?? DBNull.Value);
                ins.Parameters.AddWithValue("@idem", (object?)idempotencyKey ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync(ct);
                return newId;
            }
        }

        public async Task AppendEventRawAsync(Guid? paymentId,
                                              Guid? orgId,
                                              string eventType,
                                              string rawPayloadJson,
                                              DateTime happenedAtUtc,
                                              CancellationToken ct)
        {
            const string sql = @"
INSERT INTO dbo.payment_events(payment_id, org_id, event_type, raw_payload, happened_at_utc)
VALUES(@pid, @org, @evt, @raw, @when);";

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

}
