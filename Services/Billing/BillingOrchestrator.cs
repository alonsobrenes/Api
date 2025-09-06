// Services/Billing/BillingOrchestrator.cs
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace EPApi.Services.Billing
{
    /// <summary>
    /// Orquestador mínimo que aplica plan (suscripción + entitlements) usando el mismo SQL que hoy está en el controller.
    /// No asume nada distinto a: connection string "Default", tablas dbo.subscriptions y dbo.entitlements.
    /// </summary>
    public sealed class BillingOrchestrator
    {
        private readonly IConfiguration _cfg;
        public BillingOrchestrator(IConfiguration cfg) => _cfg = cfg;

        /// <summary>
        /// Aplica el plan y sus límites. Recibe el diccionario de entitlements ya resuelto por el caller.
        /// </summary>
        public async Task ApplySubscriptionAndEntitlementsAsync(
            Guid orgId,
            string planCodeLower,
            string statusLower,
            DateTime startUtc,
            DateTime endUtc,
            IReadOnlyDictionary<string, int> entitlements,
            CancellationToken ct)
        {
            var cs = _cfg.GetConnectionString("Default")!;
            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(ct);

            // Upsert subscription (idéntico a tu controller)
            const string upsertSub = @"
IF EXISTS (SELECT 1 FROM dbo.subscriptions WHERE org_id=@org)
BEGIN
  UPDATE dbo.subscriptions
    SET provider = N'Dummy',
        plan_code = @plan,
        status = @status,
        current_period_start_utc = @ps,
        current_period_end_utc = @pe,
        updated_at_utc = SYSUTCDATETIME()
  WHERE org_id=@org;
END
ELSE
BEGIN
  INSERT INTO dbo.subscriptions(id, org_id, provider, plan_code, status, current_period_start_utc, current_period_end_utc)
  VALUES (NEWID(), @org, N'Dummy', @plan, @status, @ps, @pe);
END";
            await using (var cmd = new SqlCommand(upsertSub, cn, (SqlTransaction)tx))
            {
                cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@plan", SqlDbType.NVarChar, 50) { Value = planCodeLower });
                cmd.Parameters.Add(new SqlParameter("@status", SqlDbType.NVarChar, 50) { Value = statusLower });
                cmd.Parameters.Add(new SqlParameter("@ps", SqlDbType.DateTime2) { Value = startUtc });
                cmd.Parameters.Add(new SqlParameter("@pe", SqlDbType.DateTime2) { Value = endUtc });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Upsert entitlements (idéntico a tu controller)
            const string upsertEnt = @"
MERGE dbo.entitlements AS t
USING (SELECT @org AS org_id, @feature AS feature_code, @limit AS limit_value) AS s
      ON (t.org_id = s.org_id AND t.feature_code = s.feature_code)
WHEN MATCHED THEN UPDATE SET t.limit_value = s.limit_value, t.updated_at_utc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (id, org_id, feature_code, limit_value)
                      VALUES (NEWID(), s.org_id, s.feature_code, s.limit_value);";

            foreach (var kv in entitlements)
            {
                await using var cmd = new SqlCommand(upsertEnt, cn, (SqlTransaction)tx);
                cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@feature", SqlDbType.NVarChar, 50) { Value = kv.Key });
                cmd.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = kv.Value });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
    }
}
