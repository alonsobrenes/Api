using EPApi.DataAccess;
using EPApi.Services.Billing;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/admin/tilopay/plans")]
public sealed class TiloPayPlansAdminController : ControllerBase
{
    private readonly IBillingGateway _gw;
    private readonly BillingRepository _billingRepo; // tu repo existente
    private readonly IHostEnvironment _env;
    private readonly ILogger<TiloPayPlansAdminController> _log;

    public TiloPayPlansAdminController(
        IBillingGateway gw,
        BillingRepository billingRepo,
        IHostEnvironment env,
        ILogger<TiloPayPlansAdminController> log)
    {
        _gw = gw;
        _billingRepo = billingRepo;
        _env = env;
        _log = log;
    }

    public sealed class CreateFromDbRequest
    {
        public string code { get; set; } = default!;
    }

    [HttpPost("create-from-db")]
    public async Task<IActionResult> CreateFromDb([FromBody] CreateFromDbRequest body, CancellationToken ct)
    {
        if (!_env.IsDevelopment()) return Forbid();
        if (string.IsNullOrWhiteSpace(body.code))
            return BadRequest(new { message = "code requerido" });

        var plan = await _billingRepo.GetPlanByCodeAsync(body.code, ct);
        if (plan == null) return NotFound(new { message = "Plan no encontrado" });
        if (!plan.IsActive) return Conflict(new { message = "Plan no activo" });

        // Crear plan en TiloPay
        var tiloId = await _gw.CreateRepeatPlanAsync(plan, ct);

        // Mapear localmente
        var updated = await _billingRepo.UpdatePlanProviderMappingAsync(plan.Code, "tilopay", tiloId, ct);

        _log.LogInformation("Plan {Code} mapeado a TiloPay id={Id}", plan.Code, tiloId);
        return Ok(new
        {
            code = plan.Code,
            provider = "tilopay",
            provider_price_id = tiloId,
            updated
        });
    }
}
