// Services/Billing/BillingOrchestrator.cs
using System.Data;
using EPApi.DataAccess;
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
        private readonly IBillingGateway _gateway;
        private readonly BillingRepository _billingRepo;
        private readonly IOrgBillingProfileRepository _orgProfileRepo;
        private readonly IOrgRepository _orgRepository;

        public BillingOrchestrator(IConfiguration cfg, IBillingGateway gateway, BillingRepository billing, IOrgBillingProfileRepository orgProfileRepo, IOrgRepository orgRepository)
        {
            _cfg = cfg;
            _gateway = gateway;
            _billingRepo = billing;
            _orgProfileRepo = orgProfileRepo;
            _orgRepository = orgRepository;
        }

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

        public async Task<(bool can, string? reason)> CanChangeToPlanAsync(Guid orgId, string planCode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(planCode))
                return (false, "planCode requerido");

            // 1) ¿El plan destino implica 1 seat?
            var seatsTarget = await _billingRepo.ResolveSeatsForPlanAsync(planCode, ct);
            if (seatsTarget.HasValue && seatsTarget.Value == 1)
            {
                // 2) Contar miembros activos de la organización
                var memberCount = await _orgRepository.CountActiveMembersAsync(orgId, ct);
                if (memberCount > 1)
                {
                    return (false,
                        $"No se puede cambiar a plan '{planCode}' con {memberCount} miembros activos. " +
                        "Reduce a 1 miembro antes de continuar.");
                }
            }

            return (true, null);
        }

        public async Task<string> GetHostedSubscriptionUrlAsync(Guid orgId, string planCode, CancellationToken ct)
        {
            var plan = await _billingRepo.GetPlanByCodeAsync(planCode, ct);
            if (plan is null) throw new InvalidOperationException("Plan inválido");
            if (!string.Equals(plan.Provider, "tilopay", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(plan.ProviderPriceId))
                throw new InvalidOperationException("Plan no mapeado a TiloPay");

            var prof = await _orgProfileRepo.GetAsync(orgId, ct);
            var email = prof?.ContactEmail
                        ?? throw new InvalidOperationException("Falta email de facturación de la organización");

            var (registerUrl, renewUrl, _) = await _gateway.GetRecurrentUrlAsync(plan.ProviderPriceId!, email, ct);
            var url = !string.IsNullOrWhiteSpace(registerUrl) ? registerUrl
                    : !string.IsNullOrWhiteSpace(renewUrl) ? renewUrl
                    : null;

            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("TiloPay no devolvió URL de registro ni de renovación");

            return url!;
        }

    }
}
