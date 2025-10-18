// Controllers/PatientSessionsController.cs
using EPApi.DataAccess;
using EPApi.Models;
using EPApi.Services;                 // IAiAssistantService, AiOpinionPromptBuilder (ya existe)
using EPApi.Services.Billing;         // IUsageService
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/patients/{patientId:guid}/sessions")]
    [Authorize]
    public sealed class PatientSessionsController : ControllerBase
    {
        private readonly IPatientSessionsRepository _repo;
        private readonly IUserRepository _users;
        private readonly ILogger<PatientSessionsController> _logger;
        private readonly IUsageService _usage;
        private readonly BillingRepository _billing;
        private readonly IAiAssistantService _ai; // NUEVO: igual que en ClinicianAttempts
        private readonly IHashtagService? _hashtag;

        public PatientSessionsController(
            IPatientSessionsRepository repo,
            IUserRepository users,
            ILogger<PatientSessionsController> logger,
            IUsageService usage,
            BillingRepository billing,
            IAiAssistantService ai,
            IHashtagService hashtag
        )
        {
            _repo = repo;
            _users = users;
            _logger = logger;
            _usage = usage;
            _billing = billing;
            _ai = ai;
            _hashtag = hashtag;
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

        /// <summary>
        /// List sessions for a patient within current org. Optional text filter and author filter.
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<PagedResult<PatientSessionDto>>> List(
            Guid patientId,
            [FromQuery] int skip = 0,
            [FromQuery] int take = 20,
            [FromQuery] string? q = null,
            [FromQuery] int? createdByUserId = null,
            CancellationToken ct = default)
        {
            var orgId = GetOrgIdOrThrow();
            var result = await _repo.ListAsync(orgId, patientId, skip, take, q, createdByUserId, ct);
            return Ok(result);
        }

        /// <summary>Get a single session by id.</summary>
        [HttpGet("{id:guid}")]
        public async Task<ActionResult<PatientSessionDto>> Get(Guid patientId, Guid id, CancellationToken ct)
        {
            var orgId = GetOrgIdOrThrow();
            var dto = await _repo.GetAsync(orgId, patientId, id, ct);
            return Ok(dto);
        }

        public sealed record CreatePatientSessionRequest(string Title, string? ContentText);
        public sealed record UpdatePatientSessionRequest(string Title, string? ContentText);
        public sealed record UpsertAiTextRequest(string? Text);

        /// <summary>Create a new session. created_by_user_id is taken from the authenticated user.</summary>
        [HttpPost]
        public async Task<ActionResult<PatientSessionDto>> Create(Guid patientId, [FromBody] CreatePatientSessionRequest body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body.Title) || body.Title.Length > 200)
                return BadRequest("Title is required (1-200 chars).");

            var orgId = GetOrgIdOrThrow();
            var userId = await GetUserIdOrResolveAsync(ct);

            var created = await _repo.CreateAsync(orgId, patientId, userId, body.Title.Trim(), body.ContentText, ct);

            if (!string.IsNullOrWhiteSpace(body.ContentText))
                await _hashtag.ExtractAndPersistAsync(orgId, "session", created.Id, body.ContentText, 5, ct);


            return CreatedAtAction(nameof(Get), new { patientId, id = created.Id }, created);
        }

        /// <summary>Update session core fields (title, content).</summary>
        [HttpPut("{id:guid}")]
        public async Task<ActionResult<PatientSessionDto>> Update(Guid patientId, Guid id, [FromBody] UpdatePatientSessionRequest body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body.Title) || body.Title.Length > 200)
                return BadRequest("Title is required (1-200 chars).");

            var orgId = GetOrgIdOrThrow();
            var updated = await _repo.UpdateAsync(orgId, patientId, id, body.Title.Trim(), body.ContentText, ct);

            await _hashtag.ExtractAndPersistAsync(orgId, "session", id, body.ContentText ?? updated.ContentText, 5, ct);

            return Ok(updated);
        }

        /// <summary>Soft-delete a session.</summary>
        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> Delete(Guid patientId, Guid id, CancellationToken ct)
        {
            var orgId = GetOrgIdOrThrow();
            await _repo.SoftDeleteAsync(orgId, patientId, id, ct);
            return NoContent();
        }

        /// <summary>Persist an already-generated tidy text for the session.</summary>
        [HttpPost("{id:guid}/ai-tidy")]
        public async Task<ActionResult<PatientSessionDto>> AiTidy(Guid patientId, Guid id, [FromBody] UpsertAiTextRequest body, CancellationToken ct)
        {
            var orgId = GetOrgIdOrThrow();
            var updated = await _repo.UpdateAiTidyAsync(orgId, patientId, id, body?.Text, ct);
            await _hashtag.ExtractAndPersistAsync(orgId, "session_tidy", id, updated.AiTidyText ?? body?.Text, 5, ct);

            return Ok(updated);
        }

        /// <summary>Persist an already-generated AI opinion text for the session.</summary>
        [HttpPost("{id:guid}/ai-opinion")]
        public async Task<ActionResult<PatientSessionDto>> AiOpinion(Guid patientId, Guid id, [FromBody] UpsertAiTextRequest body, CancellationToken ct)
        {
            var orgId = GetOrgIdOrThrow();
            var updated = await _repo.UpdateAiOpinionAsync(orgId, patientId, id, body?.Text, ct);
            await _hashtag.ExtractAndPersistAsync(orgId, "session_opinion", id, updated.AiOpinionText ?? body?.Text, 5, ct);

            return Ok(updated);
        }

        /// <summary>Generate & persist AI "tidy" (ordenar/estructurar) from current content_text.</summary>
        [HttpPost("{id:guid}/ai-tidy/auto")]
        public async Task<ActionResult<PatientSessionDto>> GenerateAiTidy(Guid patientId, Guid id, CancellationToken ct)
        {
            var orgId = GetOrgIdOrThrow();

            // 1) Cargar la sesión para obtener el texto base
            var dto = await _repo.GetAsync(orgId, patientId, id, ct);
            var source = dto?.ContentText?.Trim();
            if (string.IsNullOrWhiteSpace(source))
                return BadRequest(new { message = "No hay contenido base (content_text) para ordenar." });

            // 2) Gating (ai.credits.monthly) con hash idempotente del input
            var promptVersion = "tidy.v1";
            var inputFingerprint = $"{promptVersion}\nLEN:{source.Length}\nSHA:{Crypto.Sha256Hex(source)}";
            var inputHash = Crypto.Sha256Hex(inputFingerprint);
            

            // 3) Prompt simple — sin crear clases nuevas
            var prompt = new StringBuilder()
                .AppendLine($"[PROMPT_VERSION: {promptVersion}]")
                .AppendLine("Eres un asistente clínico. Reescribe y ordena las notas a continuación para dejarlas claras y estructuradas.")
                .AppendLine("No inventes información. Mantén el sentido clínico, usa viñetas o subtítulos cuando ayuden, y evita jergas.")
                .AppendLine()
                .AppendLine("=== NOTAS ORIGINALES ===")
                .AppendLine(source)
                .AppendLine("========================")
                .ToString();

            // 4) Llamada a IA (siguiendo patrón de ClinicianAttempts: IAiAssistantService)
            var modelVersion = "gpt-4o-mini"; // igual al default usado en ClinicianAttempts
            var ai = await _ai.GenerateOpinionAsync(prompt, modelVersion, ct); // reutilizamos método existente
            var tokens = ai.TotalTokens ?? ((ai.PromptTokens ?? 0) + (ai.CompletionTokens ?? 0));
            tokens = Math.Max(tokens, 1);


            var gate = await _usage.TryConsumeAsync(orgId, "ai.credits.monthly", tokens, $"session-tidy:{id}:{inputHash}", ct);
            if (!gate.Allowed)
                return Problem(statusCode: 402, title: "Límite del plan",
                    detail: "Has alcanzado el límite mensual para esta función de tu plan.");

            // 5) Guardar resultado
            var updated = await _repo.UpdateAiTidyAsync(orgId, patientId, id, ai.Text, ct);
            return Ok(updated);
        }

        /// <summary>Generate & persist AI opinion from current content_text.</summary>
        [HttpPost("{id:guid}/ai-opinion/auto")]
        public async Task<ActionResult<PatientSessionDto>> GenerateAiOpinion(Guid patientId, Guid id, CancellationToken ct)
        {
            var orgId = GetOrgIdOrThrow();

            // 1) Cargar la sesión (usaremos el content_text como contexto mínimo)
            var dto = await _repo.GetAsync(orgId, patientId, id, ct);
            var source = dto?.ContentText?.Trim();
            if (string.IsNullOrWhiteSpace(source))
                return BadRequest(new { message = "No hay contenido base (content_text) para opinión." });

            // 2) Gating (ai.credits.monthly) con hash idempotente del input
            var promptVersion = "session.opinion.v1";
            var inputFingerprint = $"{promptVersion}\nLEN:{source.Length}\nSHA:{Crypto.Sha256Hex(source)}";
            var inputHash = Crypto.Sha256Hex(inputFingerprint);

            
            // 3) Prompt de opinión (reutilizamos el estilo de AiOpinionPromptBuilder: síntesis prudente y no diagnóstica)
            var prompt = new StringBuilder()
                .AppendLine($"[PROMPT_VERSION: {promptVersion}]")
                .AppendLine("Eres un asistente clínico. Escribe una síntesis breve, prudente y NO diagnóstica de las notas de sesión.")
                .AppendLine("Evita jergas; puedes mencionar patrones relevantes o sesgos y cerrar con una recomendación general.")
                .AppendLine()
                .AppendLine("=== NOTAS DE SESIÓN ===")
                .AppendLine(source)
                .AppendLine("=======================")
                .ToString();

            var modelVersion = "gpt-4o-mini";
            var ai = await _ai.GenerateOpinionAsync(prompt, modelVersion, ct);
            var tokens = ai.TotalTokens ?? ((ai.PromptTokens ?? 0) + (ai.CompletionTokens ?? 0));
            tokens = Math.Max(tokens, 1);

            var gate = await _usage.TryConsumeAsync(orgId, "ai.credits.monthly", tokens, $"session-opinion:{id}:{inputHash}", ct);
            if (!gate.Allowed)
                return Problem(statusCode: 402, title: "Límite del plan",
                    detail: "Has alcanzado el límite mensual para esta función de tu plan.");


            var updated = await _repo.UpdateAiOpinionAsync(orgId, patientId, id, ai.Text, ct);
            return Ok(updated);
        }

        // Devuelve cuotas unificadas para IA (ai.credits.monthly)
        [HttpGet("ai-quotas")]
        public async Task<ActionResult<object>> GetAiQuotas(Guid patientId, CancellationToken ct)
        {
            var orgId = GetOrgIdOrThrow();
            var (start, end) = await _usage.GetCurrentPeriodUtcAsync(orgId, ct);        // periodo vigente
            var code = "ai.credits.monthly";

            var limit = await _usage.GetLimitAsync(orgId, code, ct);                     // null = ilimitado
            var used = await _billing.GetUsageForPeriodAsync(orgId, code, start, end, ct); // 0 si no hay fila
            var remaining = limit.HasValue ? Math.Max(0, limit.Value - used) : int.MaxValue;

            return Ok(new
            {
                items = new[] { new {
            code,
            limit,
            used,
            remaining,
            startUtc = start,
            endUtc   = end
        }},
                periodStartUtc = start,
                periodEndUtc = end
            });
        }


        /// <summary>Export a plain text version of the session.</summary>
        [HttpGet("{id:guid}/export")]
        public async Task<IActionResult> Export(Guid patientId, Guid id, CancellationToken ct)
        {
            var orgId = GetOrgIdOrThrow();
            var txt = await _repo.ExportPlainTextAsync(orgId, patientId, id, ct);
            var bytes = Encoding.UTF8.GetBytes(txt);
            return File(bytes, "text/plain", $"session-{id}.txt");
        }

        // --- helpers ---------------------------------------------------------
        private Guid GetOrgIdOrThrow()
        {
            var claim = User.FindFirst("org_id")?.Value;
            if (Guid.TryParse(claim, out var gid)) return gid;

            var header = Request.Headers["X-Org-Id"].FirstOrDefault();
            if (Guid.TryParse(header, out gid)) return gid;

            throw new InvalidOperationException("Missing org_id (claim or X-Org-Id header).");
        }

        private async Task<int> GetUserIdOrResolveAsync(CancellationToken ct)
        {
            var uidClaim = User.FindFirst("uid")?.Value ?? User.FindFirst("user_id")?.Value;
            if (int.TryParse(uidClaim, out var uid)) return uid;

            var email = User.FindFirst(ClaimTypes.Email)?.Value
                        ?? User.FindFirst("email")?.Value;

            var sub = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? User.FindFirst("sub")?.Value;

            if (string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(sub) && sub.Contains("@"))
                email = sub;

            if (!string.IsNullOrWhiteSpace(email))
            {
                var user = await _users.FindByEmailAsync(email!, ct);
                if (user != null) return user.Id;
            }

            var name = User.Identity?.Name;
            if (int.TryParse(name, out uid)) return uid;

            throw new InvalidOperationException("Cannot resolve current user id from claims.");
        }
    }
}
