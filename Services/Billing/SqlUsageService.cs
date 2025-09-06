// Services/Billing/SqlUsageService.cs
using System.Data;
using Microsoft.Data.SqlClient;
using EPApi.DataAccess;

namespace EPApi.Services.Billing
{
    public interface IUsageService
    {
        Task<UsageConsumeResult> TryConsumeAsync(Guid orgId, string featureCode, int delta, CancellationToken ct);
        // Overload con idempotencia (llave opcional para deduplicar consumos)
        Task<UsageConsumeResult> TryConsumeAsync(Guid orgId, string featureCode, int delta, string? idempotencyKey, CancellationToken ct);
        Task<(DateTime startUtc, DateTime endUtc)> GetCurrentPeriodUtcAsync(Guid orgId, CancellationToken ct);
        Task<int?> GetLimitAsync(Guid orgId, string featureCode, CancellationToken ct);
    }

    public sealed record UsageConsumeResult(bool Allowed, int Used, int Remaining);

    internal static class BillingPeriods
    {
        public static (DateTime startUtc, DateTime endUtc) FallbackCalendarMonthUtc()
        {
            var now = DateTime.UtcNow;
            var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return (start, start.AddMonths(1));
        }
    }

    /// <summary>
    /// Implementación SQL del consumo de cupos (usa SP dbo.usage_consume).
    /// </summary>
    public sealed class SqlUsageService : IUsageService
    {
        private readonly string _cs;
        private readonly BillingRepository _repo;

        public SqlUsageService(IConfiguration cfg, BillingRepository repo)
        {
            _cs = cfg.GetConnectionString("Default")
                  ?? throw new InvalidOperationException("Missing Default connection string");
            _repo = repo;
        }

        public async Task<(DateTime startUtc, DateTime endUtc)> GetCurrentPeriodUtcAsync(Guid orgId, CancellationToken ct)
        {
            var (start, end) = await _repo.GetSubscriptionPeriodUtcAsync(orgId, ct);
            if (start.HasValue && end.HasValue) return (start.Value, end.Value);
            return BillingPeriods.FallbackCalendarMonthUtc();
        }

        public Task<int?> GetLimitAsync(Guid orgId, string featureCode, CancellationToken ct)
            => _repo.GetEntitlementLimitAsync(orgId, featureCode, ct);


        public Task<UsageConsumeResult> TryConsumeAsync(Guid orgId, string featureCode, int delta, CancellationToken ct)
        {
            // Delegamos al overload con llave idempotente (null por defecto)
            return TryConsumeAsync(orgId, featureCode, delta, (string?)null, ct);
        }

        public async Task<UsageConsumeResult> TryConsumeAsync(
            Guid orgId,
            string featureCode,
            int delta,
            string? idempotencyKey,
            CancellationToken ct)
        {
            var (startUtc, endUtc) = await GetCurrentPeriodUtcAsync(orgId, ct);
            var limit = await GetLimitAsync(orgId, featureCode, ct); // null = ilimitado

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand("dbo.usage_consume", cn)
            {
                CommandType = CommandType.StoredProcedure
            };

            cmd.Parameters.Add(new SqlParameter("@org_id", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@feature_code", SqlDbType.NVarChar, 50) { Value = featureCode });
            cmd.Parameters.Add(new SqlParameter("@period_start_utc", SqlDbType.DateTime2) { Value = startUtc });
            cmd.Parameters.Add(new SqlParameter("@period_end_utc", SqlDbType.DateTime2) { Value = endUtc });
            cmd.Parameters.Add(new SqlParameter("@delta", SqlDbType.Int) { Value = delta });

            // Parámetro opcional de idempotencia (si el SP lo soporta)
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                cmd.Parameters.Add(new SqlParameter("@idempotency_key", SqlDbType.NVarChar, 200) { Value = idempotencyKey });
            }


            var pLimit = new SqlParameter("@limit", SqlDbType.Int);
            pLimit.Value = (object?)limit ?? DBNull.Value;
            cmd.Parameters.Add(pLimit);

            var usedOut = new SqlParameter("@used_out", SqlDbType.Int) { Direction = ParameterDirection.Output };
            var remOut = new SqlParameter("@remaining_out", SqlDbType.Int) { Direction = ParameterDirection.Output };
            var blocked = new SqlParameter("@blocked", SqlDbType.Bit) { Direction = ParameterDirection.Output };

            cmd.Parameters.Add(usedOut);
            cmd.Parameters.Add(remOut);
            cmd.Parameters.Add(blocked);

            await cmd.ExecuteNonQueryAsync(ct);

            var allowed = !(blocked.Value is bool b && b);
            var used = usedOut.Value is int u ? u : 0;
            var remaining = remOut.Value is int r ? r : 0;

            return new UsageConsumeResult(allowed, used, remaining);

        }

    }
}
