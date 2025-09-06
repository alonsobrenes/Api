// DataAccess/BillingRepository.cs
using System.Data;
using Microsoft.Data.SqlClient;

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
WHERE org_id = @org AND status IN (N'active', N'trialing')
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
    }
}
