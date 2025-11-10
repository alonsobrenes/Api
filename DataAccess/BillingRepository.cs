// DataAccess/BillingRepository.cs
using EPApi.Controllers;
using EPApi.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace EPApi.DataAccess
{
    /// <summary>
    /// Acceso a datos para Billing/Subscriptions/Entitlements.
    /// Usa Microsoft.Data.SqlClient y patrón asíncrono consistente con DataAccess.
    /// </summary>
    public sealed class BillingRepository
    {
        private readonly string _cs;

        public BillingRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                 ?? throw new InvalidOperationException("Missing Default connection string");
        }

        /// <summary>
        /// Devuelve la primera organización (org_id) a la que pertenece el usuario, o null si no está asignado.
        /// </summary>
        public async Task<Guid?> GetOrgIdForUserAsync(int userId, CancellationToken ct = default)
        {
            const string sql = @"SELECT TOP (1) org_id FROM dbo.org_members WHERE user_id = @uid;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });

            var o = await cmd.ExecuteScalarAsync(ct);
            if (o is null || o == DBNull.Value) return null;
            if (o is Guid g) return g;
            return (Guid?)o;
        }

        /// <summary>
        /// Lee el periodo de suscripción vigente (active/trialing) para una organización.
        /// Si no existe, devuelve (null, null).
        /// </summary>
        public async Task<(DateTime? startUtc, DateTime? endUtc)> GetSubscriptionPeriodUtcAsync(Guid orgId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT TOP (1) current_period_start_utc, current_period_end_utc
FROM dbo.subscriptions
WHERE org_id = @org AND status IN (N'active', N'trial', N'trialing')
ORDER BY updated_at_utc DESC;";



            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                var start = rd.IsDBNull(0) ? (DateTime?)null : rd.GetDateTime(0);
                var end = rd.IsDBNull(1) ? (DateTime?)null : rd.GetDateTime(1);
                return (start, end);
            }
            return (null, null);
        }

        /// <summary>
        /// Devuelve el límite (limit_value) del entitlement (feature) para la organización; null = ilimitado/no configurado.
        /// </summary>
        public async Task<int?> GetEntitlementLimitAsync(Guid orgId, string featureCode, CancellationToken ct = default)
        {
            const string sql = @"SELECT limit_value FROM dbo.entitlements WHERE org_id=@org AND feature_code=@f;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@f", SqlDbType.NVarChar, 50) { Value = featureCode });

            var o = await cmd.ExecuteScalarAsync(ct);
            if (o is null || o == DBNull.Value) return null;
            return Convert.ToInt32(o);
        }

        /// <summary>
        /// Devuelve el consumo (used) del período para un feature dado; si no existe fila, retorna 0.
        /// </summary>
        public async Task<int> GetUsageForPeriodAsync(Guid orgId, string featureCode, DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
        {
            const string sql = @"
SELECT used
FROM dbo.usage_counters
WHERE org_id=@org AND feature_code=@f AND period_start_utc=@start AND period_end_utc=@end;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@f", SqlDbType.NVarChar, 50) { Value = featureCode });
            cmd.Parameters.Add(new SqlParameter("@start", SqlDbType.DateTime2) { Value = startUtc });
            cmd.Parameters.Add(new SqlParameter("@end", SqlDbType.DateTime2) { Value = endUtc });

            var o = await cmd.ExecuteScalarAsync(ct);
            if (o is null || o == DBNull.Value) return 0;
            return Convert.ToInt32(o);
        }

        // --------------------------------------------------------------------
        // NUEVO: lectura directa de la última suscripción (plan y ventana).
        // --------------------------------------------------------------------
        /// <summary>
        /// Devuelve (plan_code, current_period_start_utc, current_period_end_utc) de la suscripción más reciente.
        /// Si no hay suscripciones, retorna (null, null, null).
        /// </summary>
        public async Task<(string? planCode, DateTime? startUtc, DateTime? endUtc)> GetLatestSubscriptionAsync(
            Guid orgId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT TOP (1) plan_code, current_period_start_utc, current_period_end_utc
FROM dbo.subscriptions
WHERE org_id = @org
ORDER BY updated_at_utc DESC;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                string? plan = rd.IsDBNull(0) ? null : rd.GetString(0);
                DateTime? s = rd.IsDBNull(1) ? (DateTime?)null : rd.GetDateTime(1);
                DateTime? e = rd.IsDBNull(2) ? (DateTime?)null : rd.GetDateTime(2);
                return (plan, s, e);
            }
            return (null, null, null);
        }

        // --------------------------------------------------------------------
        // NUEVO: helper de expiración de trial (para usar en endpoints de features).
        // --------------------------------------------------------------------
        /// <summary>
        /// Retorna true si la última suscripción es plan "trial" y la fecha actual es posterior al fin del periodo.
        /// </summary>
        public async Task<bool> IsTrialExpiredAsync(Guid orgId, DateTime nowUtc, CancellationToken ct = default)
        {
            var (plan, _s, e) = await GetLatestSubscriptionAsync(orgId, ct);
            if (!string.Equals(plan, "trial", StringComparison.OrdinalIgnoreCase)) return false;
            return e.HasValue && nowUtc > e.Value;
        }

        // Dentro de la clase BillingRepository:

        public async Task<List<BillingController.PlanDto>> GetPublicPlansAsync(CancellationToken ct)
        {
            var result = new List<BillingController.PlanDto>();

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            // 1) Cargar planes públicos/activos
            const string sqlPlans = @"
SELECT id, code, name, ISNULL(price_amount_cents,0) AS price_cents
FROM dbo.billing_plans
WHERE is_active = 1 AND is_public = 1
ORDER BY price_cents, name";
            var plans = new List<(Guid id, string code, string name, int priceCents)>();
            await using (var cmd = new SqlCommand(sqlPlans, cn))
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
            {
                while (await rd.ReadAsync(ct))
                {
                    plans.Add((
                        rd.GetGuid(0),
                        rd.GetString(1),
                        rd.GetString(2),
                        rd.GetInt32(3)
                    ));
                }
            }

            // 2) Para cada plan, cargar entitlements y formatear features legibles
            const string sqlEnt = @"
SELECT feature_code, limit_value
FROM dbo.billing_plan_entitlements
WHERE plan_id = @pid
ORDER BY feature_code";

            foreach (var p in plans)
            {
                var feats = new List<string>();
                await using var cmdE = new SqlCommand(sqlEnt, cn);
                cmdE.Parameters.Add(new SqlParameter("@pid", SqlDbType.UniqueIdentifier) { Value = p.id });
                await using var rdE = await cmdE.ExecuteReaderAsync(ct);
                while (await rdE.ReadAsync(ct))
                {
                    var code = rdE.GetString(0);
                    var has = !rdE.IsDBNull(1);
                    var val = has ? rdE.GetInt64(1) : 0L;

                    if (code.Equals("ai.credits.monthly", StringComparison.OrdinalIgnoreCase) && has)
                        feats.Add($"{val:N0} tokens IA/mes");
                    else if (code.Equals("stt.minutes.monthly", StringComparison.OrdinalIgnoreCase) && has)
                        feats.Add($"{val:N0} tokens transcripción IA/mes");
                    else if (code.Equals("tests.auto.monthly", StringComparison.OrdinalIgnoreCase) && has)
                        feats.Add($"{val:N0} tests auto/mes");
                    else if (code.Equals("sacks.monthly", StringComparison.OrdinalIgnoreCase) && has)
                        feats.Add($"{val:N0} SACKS/mes");
                    else if (code.Equals("seats", StringComparison.OrdinalIgnoreCase) && has)
                        feats.Add($"{val:N0} seats");
                    else if (code.Equals("storage.gb", StringComparison.OrdinalIgnoreCase) && has)
                        feats.Add($"{val:N0} GB almacenamiento");
                    else if (has)
                        feats.Add($"{code}: {val}");
                }

                var monthlyUsd = (decimal)p.priceCents / 100m;
                result.Add(new BillingController.PlanDto(p.code, p.name, monthlyUsd, feats));
            }

            return result;
        }

        public async Task<Dictionary<string, int>> GetEntitlementsByPlanCodeAsync(string planCode, CancellationToken ct)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(planCode)) return map;

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            const string sql = @"
SELECT e.feature_code, e.limit_value
FROM dbo.billing_plans p
JOIN dbo.billing_plan_entitlements e ON e.plan_id = p.id
WHERE p.code = @code";

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 50) { Value = planCode.Trim().ToLowerInvariant() });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var feature = rd.GetString(0);
                if (!rd.IsDBNull(1))
                {
                    var lim64 = rd.GetInt64(1);
                    var lim32 = lim64 > int.MaxValue ? int.MaxValue : (int)lim64;
                    map[feature] = lim32;
                }
            }

            return map;
        }

        public async Task<(string planCode, string status, int seats)?> GetOrgPlanSummaryAsync(Guid orgId, CancellationToken ct)
        {
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            const string sql = @"
WITH sub AS (
    SELECT TOP (1) plan_code, status
    FROM dbo.subscriptions
    WHERE org_id = @o AND status IN (N'active', N'trial', N'trialing')
    ORDER BY updated_at_utc DESC
)
SELECT 
    s.plan_code,
    s.status,
    CAST(ISNULL(e.limit_value, 0) AS INT) AS seats
FROM sub s
LEFT JOIN dbo.billing_plans p
    ON p.code = s.plan_code
LEFT JOIN dbo.billing_plan_entitlements e
    ON e.plan_id = p.id AND e.feature_code = N'seats';";

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.UniqueIdentifier) { Value = orgId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                var planCode = rd.IsDBNull(0) ? "solo" : rd.GetString(0);
                var status = rd.IsDBNull(1) ? "active" : rd.GetString(1);
                var seats = rd.IsDBNull(2) ? 0 : rd.GetInt32(2);
                return (planCode, status, seats);
            }

            return null;
        }

        public async Task<int?> ResolveSeatsForPlanAsync(string planCode, CancellationToken ct)
        {
            var ents = await GetEntitlementsByPlanCodeAsync(planCode, ct);
            if (ents.TryGetValue("seats", out var v))
                return v;
            return null; // si el plan no define seats explícito
        }

        public async Task<BillingPlanDto?> GetPlanByCodeAsync(string code, CancellationToken ct)
        {
            const string sql = @"
SELECT id, code, name, description, period, is_active, is_public, trial_days, currency, price_amount_cents,
       provider, provider_price_id
FROM dbo.billing_plans
WHERE code = @code;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddWithValue("@code", code);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;

            return new BillingPlanDto
            {
                Id = rd.GetGuid(0),
                Code = rd.GetString(1),
                Name = rd.GetString(2),
                Description = rd.IsDBNull(3) ? null : rd.GetString(3),
                Period = rd.GetString(4),
                IsActive = rd.GetBoolean(5),
                IsPublic = rd.GetBoolean(6),
                TrialDays = rd.GetInt32(7),
                Currency = rd.GetString(8),
                PriceAmountCents = rd.GetInt32(9),
                Provider = rd.IsDBNull(10) ? null : rd.GetString(10),
                ProviderPriceId = rd.IsDBNull(11) ? null : rd.GetString(11)
            };
        }

        public async Task<int> UpdatePlanProviderMappingAsync(string code, string provider, string providerPriceId, CancellationToken ct)
        {
            const string sql = @"
UPDATE dbo.billing_plans
SET provider = @prov,
    provider_price_id = @ppid,
    updated_at_utc = SYSUTCDATETIME()
WHERE code = @code;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddWithValue("@prov", provider);
            cmd.Parameters.AddWithValue("@ppid", providerPriceId);
            cmd.Parameters.AddWithValue("@code", code);
            return await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
