// Controllers/ClinicianAttemptsController.cs
using EPApi.DataAccess;
using EPApi.Models;
using EPApi.Services;
using EPApi.Services.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Security.Cryptography;
using static Azure.Core.HttpHeader;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/clinician/attempts")]
    [Authorize]
    public sealed class ClinicianAttemptsController : ControllerBase
    {
        private readonly IClinicianReviewRepository _repo;
        private readonly IAiAssistantService _ai;
        private readonly IUsageService _usage;
        private readonly BillingRepository _billing;
        private readonly IHostEnvironment _env;
        private readonly IHashtagService _hashtag;

        public ClinicianAttemptsController(IClinicianReviewRepository repo, IAiAssistantService ai, IUsageService usage, BillingRepository billing, IHostEnvironment env, IHashtagService hashtag)
        {
            _repo = repo;
            _ai = ai;
            _usage = usage;
            _billing = billing;
            _env = env;
            _hashtag = hashtag;
        }

        private bool TryGetUserId(out int uid)
        {
            uid = 0;
            var c = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value
                 ?? User.FindFirst("sub")?.Value
                 ?? User.FindFirst("nameid")?.Value;
            return int.TryParse(c, out uid);
        }

        private async Task<Guid> ResolveOrgIdForDevAsync(CancellationToken ct)
        {
            if (TryGetUserId(out var uid))
            {
                var org = await _billing.GetOrgIdForUserAsync(uid, ct);
                if (org is not null) return org.Value;
            }
            if (_env.IsDevelopment())
            {
                var dev = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Billing:DevOrgId"];
                if (Guid.TryParse(dev, out var g)) return g;
            }
            throw new UnauthorizedAccessException("Auth requerida o Billing:DevOrgId en Development.");
        }

        private int RequireUserId()
        {
            var idStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
            if (!int.TryParse(idStr, out var uid)) throw new UnauthorizedAccessException("No user id");
            return uid;
        }

        private async Task<Guid> RequireOrgIdAsync(CancellationToken ct)
        {
            var uid = RequireUserId();
            var org = await _billing.GetOrgIdForUserAsync(uid, ct);
            if (org is null) throw new InvalidOperationException("Usuario sin organización");
            return org.Value;
        }

        private int? GetCurrentUserId()
        {
            var raw = User.FindFirstValue("uid")
                   ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? User.FindFirstValue("sub");
            return int.TryParse(raw, out var id) ? id : (int?)null;
        }

        private bool IsAdmin()
        {
            if (User.IsInRole("Admin")) return true;
            var role = User.FindFirstValue(ClaimTypes.Role);
            return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
        }

        // DELETE /api/clinician/attempts/{attemptId}
        [HttpDelete("{attemptId:guid}")]
        public async Task<IActionResult> DeleteAttempt(Guid attemptId, CancellationToken ct)
        {
            var ownerUserId = GetCurrentUserId();
            var isAdmin = IsAdmin();

            var ok = await _repo.DeleteAttemptIfDraftAsync(attemptId, ownerUserId, isAdmin, ct);
            if (!ok) return NotFound(); // si prefieres, puedes devolver 409 cuando esté finalizado

            return NoContent();
        }
        [HttpGet("{attemptId:guid}/answers")]
        public async Task<IActionResult> GetAnswers(Guid attemptId, CancellationToken ct)
        {
            var ownerUserId = GetCurrentUserId();
            var isAdmin = IsAdmin();
            var rows = await _repo.GetAttemptAnswersAsync(attemptId, ownerUserId, isAdmin, ct);
            return Ok(rows); // esperado por el cliente
        }

        public sealed class CreateAttemptRequest
        {
            public Guid TestId { get; set; }
            public Guid? PatientId { get; set; }
            public List<AttemptAnswerWriteDto>? Answers { get; set; }
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateAttemptRequest body, CancellationToken ct)
        {
            if (body is null || body.TestId == Guid.Empty)
                return BadRequest(new { message = "TestId requerido" });

            var uid = GetCurrentUserId();
            if (uid is null) return Forbid();

            var res = await _repo.CreateAttemptAsync(body.TestId, body.PatientId, uid.Value, ct);

            if (body.Answers is { Count: > 0 })
            {
                await _repo.UpsertAttemptAnswersAsync(res.AttemptId, body.Answers, ct);
            }

            return Ok(new { attemptId = res.AttemptId });
        }

        [HttpPost("log-auto")]
        public async Task<IActionResult> LogAuto([FromBody] LogAutoAttemptDto dto, CancellationToken ct)
        {
            if (dto == null || dto.TestId == Guid.Empty)
                return BadRequest(new { message = "TestId es obligatorio" });

            var uid = GetCurrentUserId();
            if (uid is null) return Forbid();

            // Si quieres restringir por usuario actual, úsalo como en tus otros endpoints
            var attemptId = await _repo.LogAutoAttemptAsync(dto.TestId, dto.PatientId, dto.StartedAtUtc, uid.Value, ct);
            return Ok(new { attemptId });
        }

        // POST /api/clinician/attempts/{attemptId}/answers
        [HttpPost("{attemptId:guid}/answers")]
        public async Task<IActionResult> UpsertAnswers(Guid attemptId, [FromBody] List<AttemptAnswerWriteDto> items, CancellationToken ct)
        {
            if (items == null) items = new List<AttemptAnswerWriteDto>();
            // (opcional) validar ownership/admin si quieres
            await _repo.UpsertAttemptAnswersAsync(attemptId, items, ct);
            return NoContent();
        }

        // GET /api/clinician/attempts/{attemptId}/meta
        [HttpGet("{attemptId:guid}/meta")]
        public async Task<IActionResult> GetMeta(Guid attemptId, CancellationToken ct)
        {
            var meta = await _repo.GetAttemptMetaAsync(attemptId, ct);
            return meta is null ? NotFound() : Ok(meta);
        }
        // POST /api/clinician/attempts/{attemptId}/finalize
        [HttpPost("{attemptId:guid}/finalize")]
        public async Task<IActionResult> FinalizeAttempt(Guid attemptId, CancellationToken ct)
        {
            await _repo.FinalizeAttemptAsync(attemptId, ct);
            return NoContent();
        }
        // GET /api/clinician/attempts/summary?dateFrom=...&dateTo=...
        // GET /api/clinician/attempts/summary?dateFrom=...&dateTo=...
        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary(
            [FromQuery] DateTime? dateFrom = null,
            [FromQuery] DateTime? dateTo = null,
            CancellationToken ct = default)
        {
            var to = dateTo ?? DateTime.UtcNow;
            var from = dateFrom ?? to.AddDays(-6);

            int? userId = null;
            var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (int.TryParse(claim, out var parsed)) userId = parsed;
            var isAdmin = User.IsInRole("Admin") || User.IsInRole("Owner") || User.IsInRole("Manager");

            var dto = await _repo.GetAttemptSummaryAsync(from, to, userId, isAdmin, ct);
            return Ok(dto); // { total, finished }
        }

        // === AI Opinion per Attempt (additive, non-breaking) ===
        public sealed class UpsertOpinionRequest
        {
            public Guid PatientId { get; set; }
            public string? OpinionText { get; set; }
            public string? OpinionJson { get; set; }
            public string? ModelVersion { get; set; }
            public string? PromptVersion { get; set; }
            public string? InputHash { get; set; }
            public byte? RiskLevel { get; set; }
            public int? PromptTokens { get; set; }
            public int? CompletionTokens { get; set; }
            public int? TotalTokens { get; set; }
        }

        [HttpGet("{attemptId:guid}/ai-opinion")]
        public async Task<IActionResult> GetAiOpinion(Guid attemptId, CancellationToken ct)
        {
            var dto = await _repo.GetAiOpinionByAttemptAsync(attemptId, ct);
            return dto is null ? Ok(new { }) : Ok(dto);
        }



        // PUT /api/clinician/attempts/{attemptId}/ai-opinion
        [HttpPut("{attemptId:guid}/ai-opinion")]
        public async Task<IActionResult> UpsertAiOpinion(Guid attemptId, [FromBody] UpsertOpinionRequest body, CancellationToken ct)
        {
            if (body == null || body.PatientId == Guid.Empty)
                return BadRequest(new { message = "PatientId es obligatorio" });

            // En IClinicianReviewRepository:
            // Task UpsertAiOpinionAsync(Guid attemptId, Guid patientId, string? text, string? json,
            //   string? model, string? promptVersion, string? inputHash, byte? risk, CancellationToken ct);
            await _repo.UpsertAiOpinionAsync(
                attemptId,
                body.PatientId,
                body.OpinionText,
                body.OpinionJson,
                body.ModelVersion,
                body.PromptVersion,
                body.InputHash,
                body.RiskLevel,
                body.PromptTokens,
                body.CompletionTokens,
                body.TotalTokens,                
                ct
            );

            var orgId = await RequireOrgIdAsync(ct);
            await _hashtag.ExtractAndPersistAsync(orgId, "attempt_review", attemptId, body.OpinionText, 5, ct);


            return Ok(new { saved = true });
        }

        // DTO opcional para parametrizar el modelo/prompt
        public sealed class GenerateAiOpinionRequest
        {
            public string? Model { get; set; }          // p.ej. "gpt-4o-mini"
            public string? PromptVersion { get; set; }  // p.ej. "v1.0"
        }

        // POST /api/clinician/attempts/{attemptId}/ai-opinion/auto
        [HttpPost("{attemptId:guid}/ai-opinion/auto")]
        public async Task<IActionResult> GenerateAiOpinion(Guid attemptId,
            [FromBody] GenerateAiOpinionRequest? req,
            CancellationToken ct)
        {
            // 1) Traer el "bundle" necesario para el prompt
            //    (implementa este método en el repo usando JOINs ya existentes)
            //    Debe traer: patientId, test info del intento actual (con escalas),
            //    texto de entrevista inicial (si hay), y un resumen de tests previos (escalas).
            var bundle = await _repo.GetAttemptBundleForAiAsync(attemptId, ct);
            if (bundle is null) return NotFound(new { message = "Attempt no encontrado" });

            // 2) Construir prompt; fingerprint para evitar recomputar
            var promptVersion = string.IsNullOrWhiteSpace(req?.PromptVersion) ? "v1.0" : req!.PromptVersion!;
            var modelVersion = string.IsNullOrWhiteSpace(req?.Model) ? "gpt-4o-mini" : req!.Model!;
            var inputFingerprint = AiOpinionPromptBuilder.Fingerprint(bundle, promptVersion); // string estable
            var inputHash = Crypto.Sha256Hex(inputFingerprint);

            // Si ya existe la misma opinión (mismo hash), la devolvemos
            var prev = await _repo.GetAiOpinionByAttemptAsync(attemptId, ct);
            if (prev != null && string.Equals(prev.InputHash, inputHash, StringComparison.Ordinal))
                return Ok(prev);

            //3 Valida que no haya consumido su cuota mensual
            var orgId = await RequireOrgIdAsync(ct);

            if (await _billing.IsTrialExpiredAsync(orgId, DateTime.UtcNow, ct))
                return Problem(statusCode: 402, title: "Período de prueba",
                    detail: "Tu período de prueba expiró. Elige un plan para continuar.");

            
            // 4) Llamar a IA (inyecta tu servicio de IA por DI: IAssistantService / IOpenAIService, etc.)
            var prompt = AiOpinionPromptBuilder.Build(bundle, promptVersion);
            var ai = await _ai.GenerateOpinionAsync(prompt, modelVersion, ct);
            var tokens = ai.TotalTokens ?? ((ai.PromptTokens ?? 0) + (ai.CompletionTokens ?? 0));
            tokens = Math.Max(tokens, 1);

            var key = $"aiopinion:{attemptId}:{inputHash}";
            var gate = await _usage.TryConsumeAsync(orgId, "ai.credits.monthly", tokens, key, ct);

            if (!gate.Allowed)
            {
                return Problem(statusCode: 402, title: "Límite del plan",
                    detail: "Has alcanzado el límite mensual de créditos de IA para tu plan.");
            }
            

            // 4) Guardar
            await _repo.UpsertAiOpinionAsync(
                attemptId,
                bundle.PatientId,
                ai.Text,
                ai.Json,
                modelVersion,
                promptVersion,
                inputHash,
                ai.RiskLevel,
                ai.PromptTokens,
                ai.CompletionTokens,
                ai.TotalTokens,
                ct
            );

            await _hashtag.ExtractAndPersistAsync(orgId, "attempt_review", attemptId, ai.Text, 5, ct);

            return Ok(new
            {
                opinionText = ai.Text,
                opinionJson = ai.Json,
                modelVersion,
                promptVersion,
                inputHash,
                riskLevel = ai.RiskLevel,
                createdAtUtc = DateTime.UtcNow
            });
        }



    }
}
