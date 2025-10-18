using EPApi.DataAccess;
using EPApi.Services;
using EPApi.Services.Billing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using static EPApi.Controllers.AuthController;

namespace EPApi.Services
{
    public sealed class RegistrationService : IRegistrationService
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<RegistrationService> _logger;
        private readonly BillingOrchestrator _orchestrator;
        private readonly BillingRepository _billingRepo;

        public RegistrationService(
            IConfiguration cfg,
            ILogger<RegistrationService> logger,
            BillingOrchestrator orchestrator,
            BillingRepository billingRepo)
        {
            _cfg = cfg;
            _logger = logger;
            _orchestrator = orchestrator;
            _billingRepo = billingRepo;
        }

       
        // ========= Nueva firma con planCode =========
        public async Task<Guid> RegisterAsync(RegisterRequest registerRequest, int userId, CancellationToken ct = default)
        {
            // 1) Crear usuario y organización (lógica existente)
            var orgId = await CreateUserAndOrgAsync(registerRequest, userId, ct);

            // 2) Determinar plan de trial
            var plan = (registerRequest.PlanCode ?? _cfg["Billing:DefaultSignupPlanCode"] ?? _cfg["Billing:TrialPlanCode"] ?? "solo")
                .Trim()
                .ToLowerInvariant();

            // 3) Validar plan público/activo
            if (!await IsPublicActivePlanAsync(plan, ct))
            {
                _logger.LogWarning("PlanCode '{Plan}' no válido. Se usará fallback 'solo'.", plan);
                plan = "solo";
            }

            // 4) Cargar entitlements desde BD
            var ent = await _billingRepo.GetEntitlementsByPlanCodeAsync(plan, ct);
            if (ent.Count == 0)
            {
                // Si el plan no tiene entitlements configurados, no forzar trial
                _logger.LogWarning("Plan '{Plan}' sin entitlements en BD. Se omite provisión de trial.", plan);
                return (orgId);
            }

            // 5) Aplicar suscripción TRIAL (período mensual UTC)
            var now = DateTime.UtcNow;
            var startUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var endUtc = startUtc.AddMonths(1);

            await _orchestrator.ApplySubscriptionAndEntitlementsAsync(
                orgId: orgId,
                planCodeLower: plan,
                statusLower: "trial",
                startUtc: startUtc,
                endUtc: endUtc,
                entitlements: ent,
                ct: ct
            );

            _logger.LogInformation("Registro con trial aplicado. Org={OrgId}, Plan={Plan}", orgId, plan);
            return (orgId);
        }

        // ==================== Helpers existentes/adaptados ====================

        private async Task<Guid> CreateUserAndOrgAsync(RegisterRequest request, int userId, CancellationToken ct)
        {           
            var cs = _cfg.GetConnectionString("Default")!;
            Guid orgId = Guid.NewGuid();

            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(ct);

            const string SQL_INSERT_ORG = @"
INSERT INTO dbo.orgs (id, name)
VALUES (@id, @name);";

            await using (var cmd = new SqlCommand(SQL_INSERT_ORG, cn, (SqlTransaction)tx))
            {
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 200) { Value = request.Email});
                await cmd.ExecuteNonQueryAsync(ct);
            }
            
            using (var cmd = new SqlCommand(@"
INSERT INTO dbo.org_members(org_id, user_id, role, created_at_utc) 
VALUES (@o, @u, N'owner', SYSUTCDATETIME());", cn, (SqlTransaction)tx))
            {
                cmd.Parameters.Add(new SqlParameter("@o", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@u", SqlDbType.Int) { Value = userId });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            return (orgId);
        }

        private async Task<bool> IsPublicActivePlanAsync(string planCodeLower, CancellationToken ct)
        {
            var cs = _cfg.GetConnectionString("Default")!;
            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync(ct);

            const string sql = @"
SELECT TOP (1) 1
FROM dbo.billing_plans
WHERE code = @c AND is_active = 1 AND is_public = 1;";

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@c", SqlDbType.NVarChar, 50) { Value = planCodeLower });
            var obj = await cmd.ExecuteScalarAsync(ct);
            return obj != null;
        }

//        private async Task<Dictionary<string, int>> LoadEntitlementsByPlanCodeAsync(string planCodeLower, CancellationToken ct)
//        {
//            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
//            var cs = _cfg.GetConnectionString("Default")!;
//            await using var cn = new SqlConnection(cs);
//            await cn.OpenAsync(ct);

//            const string sql = @"
//SELECT e.feature_code, e.limit_value
//FROM dbo.billing_plans p
//JOIN dbo.billing_plan_entitlements e ON e.plan_id = p.id
//WHERE p.code = @code;";

//            await using var cmd = new SqlCommand(sql, cn);
//            cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 50) { Value = planCodeLower });

//            await using var rd = await cmd.ExecuteReaderAsync(ct);
//            while (await rd.ReadAsync(ct))
//            {
//                var feature = rd.GetString(0);
//                if (!rd.IsDBNull(1))
//                {
//                    var lim64 = rd.GetInt64(1);
//                    var lim32 = lim64 > int.MaxValue ? int.MaxValue : (int)lim64;
//                    result[feature] = lim32;
//                }
//            }
//            return result;
//        }
    }        
}
