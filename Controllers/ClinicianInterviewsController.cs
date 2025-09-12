using EPApi.DataAccess;
using EPApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Claims;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/clinician/interviews")]
    [Authorize] // si tu API usa auth
    public sealed class ClinicianInterviewsController : ControllerBase
    {
        private readonly IInterviewsRepository _repo;
        private readonly ITranscriptionService _stt;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _cfg;
        private readonly IMemoryCache _cache;
        private readonly IInterviewDraftService? _drafts;
        private readonly BillingRepository _billing;

        public ClinicianInterviewsController(
            IInterviewsRepository repo,
            ITranscriptionService stt,
            IWebHostEnvironment env,
            IConfiguration cfg,
            IMemoryCache cache,
            IInterviewDraftService? drafts = null, // opcional para que compile aunque no esté registrado aún
            BillingRepository? billing = null
        )
        {
            _repo = repo;
            _stt = stt;
            _env = env;
            _cfg = cfg;
            _cache = cache;
            _drafts = drafts;
            _billing = billing ?? HttpContext?.RequestServices?.GetService<BillingRepository>()
                        ?? throw new InvalidOperationException("BillingRepository no disponible en DI.");
        }

        // Crea una entrevista vacía y devuelve su Id
        public sealed record CreateInterviewRequest(Guid PatientId);

        private int RequireUserId()
        {
            var raw = User.FindFirstValue("uid")
                    ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.FindFirstValue("sub");
            if (int.TryParse(raw, out var id)) return id;
            throw new UnauthorizedAccessException("No user id");
        }

        private async Task<Guid> RequireOrgIdAsync(CancellationToken ct)
        {
            var uid = RequireUserId();
            var org = await _billing.GetOrgIdForUserAsync(uid, ct);
            if (org is null) throw new InvalidOperationException("Usuario sin organización");
            return org.Value;
        }


        [HttpPost] // POST /api/clinician/interviews
        public async Task<IActionResult> Create([FromBody] CreateInterviewRequest body, CancellationToken ct)
        {
            if (body is null || body.PatientId == Guid.Empty)
                return BadRequest("Falta patientId.");

            // === Trial expirado → 402 (igual patrón que en otros endpoints)
            var orgId = await RequireOrgIdAsync(ct);
            if (await _billing.IsTrialExpiredAsync(orgId, DateTime.UtcNow, ct))
                return StatusCode(402, new { message = "Tu período de prueba expiró. Elige un plan para continuar." });

            var id = await _repo.CreateInterviewAsync(body.PatientId, ct);
            // Opcional: estado inicial
            await _repo.SetStatusAsync(id, "new", ct);

            return Ok(new { id });
        }


        // ========================= AUDIO UPLOAD =========================

        [HttpPost("{id:guid}/audio")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(500_000_000)]
        public async Task<IActionResult> UploadAudio(Guid id, [FromForm] Microsoft.AspNetCore.Http.IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0) return BadRequest("Archivo vacío.");

            // === Trial expirado → 402 (igual patrón que en otros endpoints)
            var orgId = await RequireOrgIdAsync(ct);
            if (await _billing.IsTrialExpiredAsync(orgId, DateTime.UtcNow, ct))
                return StatusCode(402, new { message = "Tu período de prueba expiró. Elige un plan para continuar." });

            var ctLower = (file.ContentType ?? "").ToLowerInvariant();
            var isAudio =
                ctLower.StartsWith("audio/") ||
                ctLower.Contains("webm") ||
                ctLower.Contains("wav") ||
                ctLower.Contains("ogg") ||
                ctLower.Contains("mpeg") || ctLower.Contains("mp3") ||
                ctLower.Contains("mp4") || ctLower.Contains("m4a") || ctLower.Contains("aac");

            if (!isAudio)
                return BadRequest($"Tipo de archivo no soportado: {file.ContentType}");

            string ext =
                ctLower.Contains("webm") ? ".webm" :
                ctLower.Contains("wav") ? ".wav" :
                ctLower.Contains("ogg") ? ".ogg" :
                (ctLower.Contains("mpeg") || ctLower.Contains("mp3")) ? ".mp3" :
                (ctLower.Contains("mp4") || ctLower.Contains("m4a") || ctLower.Contains("aac")) ? ".m4a" :
                ".bin";

            var root = _cfg.GetValue<string>("Uploads:Root")
                       ?? Path.Combine(_env.ContentRootPath, "wwwroot", "uploads");

            var folder = Path.Combine(root, "interviews", id.ToString("N"));
            Directory.CreateDirectory(folder);

            var fname = Guid.NewGuid().ToString("N") + ext;
            var absPath = Path.Combine(folder, fname);

            await using (var fs = System.IO.File.Create(absPath))
                await file.CopyToAsync(fs, ct);

            var rel = $"/uploads/interviews/{id.ToString("N")}/{fname}";
            await _repo.AddAudioAsync(id, rel, file.ContentType, null, ct);
            await _repo.SetStatusAsync(id, "uploaded", ct);

            return Ok(new { uri = rel });
        }

        // ========================= TRANSCRIBIR ==========================

        // Enfriamiento para evitar martillar proveedor por doble clic
        private bool TryAcquireTranscribeCooldown(Guid interviewId, int seconds, out int remainSec)
        {
            var key = $"transcribe-lock:{interviewId}";
            if (_cache.TryGetValue<DateTime>(key, out var until) && until > DateTime.UtcNow)
            {
                remainSec = Math.Max(1, (int)(until - DateTime.UtcNow).TotalSeconds);
                return false;
            }
            _cache.Set(key, DateTime.UtcNow.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));
            remainSec = seconds;
            return true;
        }

        [HttpPost("{id:guid}/transcribe")]
        public async Task<IActionResult> Transcribe(Guid id, [FromQuery] bool force = false, CancellationToken ct = default)
        {
            // cooldown 20s (solo si no se fuerza)
            if (!force && !TryAcquireTranscribeCooldown(id, 20, out var _))
                return StatusCode(429, new { message = "Operación en enfriamiento.", retryAfterSec = 20 });

            // === Trial expirado → 402 (igual patrón que en otros endpoints)
            var orgId = await RequireOrgIdAsync(ct);
            if (await _billing.IsTrialExpiredAsync(orgId, DateTime.UtcNow, ct))
                return StatusCode(402, new { message = "Tu período de prueba expiró. Elige un plan para continuar." });

            try
            {
                // Cache: si ya existe y no pidieron force, devolvemos último texto
                if (!force)
                {
                    var cached = await _repo.GetLatestTranscriptTextAsync(id, ct);
                    if (!string.IsNullOrWhiteSpace(cached))
                        return Ok(new { language = "es", text = cached, cached = true });
                }

                // Localiza el archivo: BD -> disco; si no, recorre carpeta
                var latest = await _repo.GetLatestAudioAsync(id, ct);
                string? absPath = null;

                if (latest is not null)
                {
                    absPath = ResolveAbsoluteUploadPath(latest.Value.Uri);
                    if (!System.IO.File.Exists(absPath)) absPath = null;
                }

                if (absPath is null)
                {
                    var root = _cfg.GetValue<string>("Uploads:Root")
                               ?? Path.Combine(_env.ContentRootPath, "wwwroot", "uploads");
                    var folder = Path.Combine(root, "interviews", id.ToString("N"));

                    if (!Directory.Exists(folder))
                        return BadRequest("No hay audio registrado para esta entrevista (ni archivos en disco).");

                    var files = new DirectoryInfo(folder).GetFiles();
                    if (files.Length == 0)
                        return BadRequest("No hay audio registrado para esta entrevista (carpeta vacía).");

                    Array.Sort(files, (a, b) => b.CreationTimeUtc.CompareTo(a.CreationTimeUtc));
                    absPath = files[0].FullName;

                    // Si BD no tenía fila, registra ahora
                    if (latest is null)
                    {
                        var rootNorm = (_cfg.GetValue<string>("Uploads:Root")
                                         ?? Path.Combine(_env.ContentRootPath, "wwwroot", "uploads"))
                                       .TrimEnd('\\', '/');

                        var relUri = absPath.Replace(rootNorm, "").Replace("\\", "/");
                        if (!relUri.StartsWith("/")) relUri = "/" + relUri;

                        await _repo.AddAudioAsync(id, relUri, GetMimeFromExtension(Path.GetExtension(absPath)), null, ct);
                    }
                }

                await _repo.SetStatusAsync(id, "transcribing", ct);

                var (lang, text, wordsJson) = await _stt.TranscribeAsync(absPath!, ct);

                await _repo.SaveTranscriptAsync(id, lang, text, wordsJson, ct);
                return Ok(new { language = lang, text, cached = false });
            }
            catch (RateLimitException rlex)
            {
                return StatusCode(429, new { message = rlex.Message, retryAfterSec = rlex.RetryAfterSeconds });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Transcribe] Interview={id} Error={ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
                return StatusCode(500, $"Transcripción fallida: {ex.Message}");
            }
        }

        // ================= GENERAR BORRADOR (IA) =======================

        public sealed record GenerateDiagnosisRequest(string? PromptVersion);

        [HttpPost("{id:guid}/diagnosis")]
        public async Task<IActionResult> GenerateDiagnosis(Guid id, [FromBody] GenerateDiagnosisRequest body, CancellationToken ct)
        {
            if (_drafts is null)
                return StatusCode(503, "Servicio de IA no disponible en este entorno.");

            // === Trial expirado → 402 (igual patrón que en otros endpoints)
            var orgId = await RequireOrgIdAsync(ct);
            if (await _billing.IsTrialExpiredAsync(orgId, DateTime.UtcNow, ct))
                return StatusCode(402, new { message = "Tu período de prueba expiró. Elige un plan para continuar." });

            var promptVersion = body?.PromptVersion ?? "v1";
            var content = await _drafts.GenerateDraftAsync(id, promptVersion, ct);

            

            await _repo.SaveDraftAsync(id, content, _cfg["OpenAI:Model"] ?? "gpt-4o-mini", promptVersion, ct);

            return Ok(new { content });
        }

        // ======================= Helpers ===============================

        private string ResolveAbsoluteUploadPath(string uri)
        {
            var root = _cfg.GetValue<string>("Uploads:Root")
                       ?? Path.Combine(_env.ContentRootPath, "wwwroot", "uploads");

            string cleaned = (uri ?? "").Replace('\\', '/');
            if (cleaned.StartsWith("/")) cleaned = cleaned.Substring(1);
            if (cleaned.StartsWith("uploads/")) cleaned = cleaned.Substring("uploads/".Length);

            var abs = Path.Combine(root, cleaned.Replace('/', Path.DirectorySeparatorChar));

            if (!System.IO.File.Exists(abs))
            {
                var webroot = Path.Combine(_env.ContentRootPath, "wwwroot");
                var alt = Path.Combine(webroot, cleaned.Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(alt)) return alt;
            }
            return abs;
        }

        private static string GetMimeFromExtension(string ext)
        {
            ext = (ext ?? "").ToLowerInvariant();
            return ext switch
            {
                ".webm" => "audio/webm",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                _ => "application/octet-stream"
            };
        }

        // Guarda un borrador IA enviado desde el front
        public sealed record SaveDraftRequest(string Content, string? Model, string? PromptVersion);

        [HttpPut("{id:guid}/draft")]
        public async Task<IActionResult> SaveDraft(Guid id, [FromBody] SaveDraftRequest body, CancellationToken ct)
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Content))
                return BadRequest("Contenido vacío.");

            // === Trial expirado → 402 (igual patrón que en otros endpoints)
            var orgId = await RequireOrgIdAsync(ct);
            if (await _billing.IsTrialExpiredAsync(orgId, DateTime.UtcNow, ct))
                return StatusCode(402, new { message = "Tu período de prueba expiró. Elige un plan para continuar." });

            await _repo.SaveDraftAsync(id, body.Content, body.Model, body.PromptVersion, ct);
            return Ok(new { saved = true });
        }

        // Guarda una transcripción manual (texto)
        public sealed record SaveTranscriptRequest(string? Language, string Text, string? WordsJson);

        [HttpPut("{id:guid}/transcript")]
        public async Task<IActionResult> SaveTranscript(Guid id, [FromBody] SaveTranscriptRequest body, CancellationToken ct)
        {
            var orgId = await RequireOrgIdAsync(ct);
            if (await _billing.IsTrialExpiredAsync(orgId, DateTime.UtcNow, ct))
                return StatusCode(402, new { message = "Tu período de prueba expiró. Elige un plan para continuar." });

            var lang = string.IsNullOrWhiteSpace(body?.Language) ? "es" : body!.Language!;
            var text = body?.Text ?? string.Empty;
            await _repo.SaveTranscriptAsync(id, lang, text, body?.WordsJson, ct);
            return Ok(new { saved = true });
        }


        // Guarda diagnóstico del profesional
        public sealed record SaveClinicianDiagnosisRequest(string? Text, bool Close);

        [HttpPut("{id:guid}/clinician-diagnosis")]
        public async Task<IActionResult> SaveClinicianDiagnosis(Guid id, [FromBody] SaveClinicianDiagnosisRequest body, CancellationToken ct)
        {
            var orgId = await RequireOrgIdAsync(ct);
            if (await _billing.IsTrialExpiredAsync(orgId, DateTime.UtcNow, ct))
                return StatusCode(402, new { message = "Tu período de prueba expiró. Elige un plan para continuar." });

            await _repo.UpdateClinicianDiagnosisAsync(id, body?.Text ?? string.Empty, body?.Close == true, ct);
            return Ok(new { saved = true, closed = body?.Close == true });
        }


        public sealed record FirstInterviewDto(Guid InterviewId, DateTime? StartedAtUtc, string? Status, string? TranscriptText, string? DraftContent, string? ClinicianDiagnosis);

        [HttpGet("patient/{patientId:guid}/first")]
        public async Task<IActionResult> GetFirstByPatient(Guid patientId, CancellationToken ct)
        {
            var dto = await _repo.GetFirstInterviewByPatientAsync(patientId, ct);
            //if (dto is null) return NotFound();
            return Ok(dto);
        }


    }



    // Excepción para comunicar 429 de forma controlada
    public sealed class RateLimitException : Exception
    {
        public int? RetryAfterSeconds { get; }
        public RateLimitException(string message, int? retryAfterSeconds = null) : base(message)
        {
            RetryAfterSeconds = retryAfterSeconds;
        }
    }
}
