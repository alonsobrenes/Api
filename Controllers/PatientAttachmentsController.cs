// NUEVO: acceso a datos de billing para chequear expiración de trial
using EPApi.DataAccess;
using EPApi.Models;
using EPApi.Services;
using EPApi.Services.Billing;
using EPApi.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/patients")]
    [Authorize]
    public sealed class PatientAttachmentsController : ControllerBase
    {
        private readonly IStorageService _storage;
        private readonly StorageOptions _options;
        private readonly IHashtagService _hashtag;
        private readonly string _cs;
        private readonly IUsageService _usage;

        // NUEVO: repo para leer suscripción/trial
        private readonly BillingRepository _billingRepo;

        public PatientAttachmentsController(
            IStorageService storage,
            IOptions<StorageOptions> options,
            IConfiguration cfg,
            // NUEVO: inyectamos el repo (ya existe en tu DI)
            BillingRepository billingRepo,
            IHashtagService hashtag,
            IUsageService usage
        )
        {
            _storage = storage;
            _options = options.Value;
            _cs = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing Default connection string");
            _billingRepo = billingRepo;
            _hashtag = hashtag;
            _usage = usage;
        }

        // ===== Helpers =====
        private int? GetCurrentUserId()
        {
            // Same pattern used in PatientsController
            var raw = User.FindFirstValue("uid")
                   ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? User.FindFirstValue("sub");
            if (int.TryParse(raw, out var id)) return id;
            return null;
        }

        private Guid? TryGetOrgClaim()
        {
            var val = User.FindFirstValue("org_id");
            if (Guid.TryParse(val, out var g)) return g;
            return null;
        }

        private async Task<Guid?> ResolveOrgIdAsync(int userId, CancellationToken ct)
        {
            // If claim present, trust it but validate membership
            var claimOrg = TryGetOrgClaim();
            if (claimOrg is Guid cg)
            {
                if (await IsUserInOrgAsync(userId, cg, ct)) return cg;
                return null;
            }

            // Otherwise, load memberships
            const string SQL = @"SELECT org_id FROM dbo.org_members WHERE user_id = @uid;";
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.Add(new SqlParameter("@uid", System.Data.SqlDbType.Int) { Value = userId });
            using var rd = await cmd.ExecuteReaderAsync(ct);

            Guid? single = null;
            int count = 0;
            while (await rd.ReadAsync(ct))
            {
                count++;
                var g = rd.GetGuid(0);
                if (count == 1) single = g;
            }

            if (count == 1) return single;

            // If multiple or none, allow override via header X-Org-Id (validated)
            if (Request.Headers.TryGetValue("X-Org-Id", out var hdr) && Guid.TryParse(hdr.FirstOrDefault(), out var fromHeader))
            {
                if (await IsUserInOrgAsync(userId, fromHeader, ct)) return fromHeader;
            }

            return null;
        }

        private async Task<bool> IsUserInOrgAsync(int userId, Guid orgId, CancellationToken ct)
        {
            const string SQL = @"SELECT 1 FROM dbo.org_members WHERE user_id = @uid AND org_id = @org;";
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.Add(new SqlParameter("@uid", System.Data.SqlDbType.Int) { Value = userId });
            cmd.Parameters.Add(new SqlParameter("@org", System.Data.SqlDbType.UniqueIdentifier) { Value = orgId });
            var res = await cmd.ExecuteScalarAsync(ct);
            return res != null;
        }

        private async Task<bool> PatientBelongsToOrgAsync(Guid patientId, Guid orgId, CancellationToken ct)
        {
            const string SQL = @"
SELECT 1
FROM dbo.patients p
JOIN dbo.org_members m ON m.user_id = p.created_by_user_id
WHERE p.id = @pid AND m.org_id = @org;";
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.Add(new SqlParameter("@pid", System.Data.SqlDbType.UniqueIdentifier) { Value = patientId });
            cmd.Parameters.Add(new SqlParameter("@org", System.Data.SqlDbType.UniqueIdentifier) { Value = orgId });
            var res = await cmd.ExecuteScalarAsync(ct);
            return res != null;
        }

        private bool IsAllowedContentType(string contentType)
        {
            if (_options.AllowedContentTypes is null || _options.AllowedContentTypes.Count == 0)
                return true;
            return _options.AllowedContentTypes.Contains(contentType);
        }

        // ===== 1) List =====
        [HttpGet("{patientId:guid}/attachments")]
        public async Task<IActionResult> List(Guid patientId, CancellationToken ct)
        {
            var userId = GetCurrentUserId();
            if (userId is null) return Forbid();

            // En List estás usando OrgResolver centralizado; respetamos eso
            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);

            if (!await PatientBelongsToOrgAsync(patientId, orgId, ct))
                return NotFound(new { message = "Paciente no pertenece a su organización. " + "patientID: " + patientId.ToString() + ", orgID= " + orgId.ToString() });

            var items = await _storage.ListAsync(orgId, patientId, ct);
            return Ok(items);
        }

        // ===== 2) Upload =====
        [HttpPost("{patientId:guid}/attachments")]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> Upload(Guid patientId, [FromForm] IFormFile file, [FromForm] string? comment, CancellationToken ct)
        {
            var userId = GetCurrentUserId();
            if (userId is null) return Forbid();

            if (file is null || file.Length == 0)
                return BadRequest(new { message = "Archivo requerido." });

            if (_options.MaxFileSizeMB > 0 && file.Length > (long)_options.MaxFileSizeMB * 1024L * 1024L)
                return StatusCode(413, new { message = $"El archivo excede el tamaño máximo permitido ({_options.MaxFileSizeMB} MB)." });

            var contentType = !string.IsNullOrWhiteSpace(file.ContentType) ? file.ContentType : "application/octet-stream";
            if (!IsAllowedContentType(contentType))
                return BadRequest(new { message = "Tipo de archivo no permitido.", contentType });
            
            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);

            if (!await PatientBelongsToOrgAsync(patientId, orgId, ct))
                return NotFound(new { message = "Paciente no pertenece a su organización." });

            if (await _billingRepo.IsTrialExpiredAsync(orgId, DateTime.UtcNow, ct))
            {
                return StatusCode(402, new { message = "Tu período de prueba expiró. Elige un plan para continuar." });
            }

            await using var stream = file.OpenReadStream();
            try
            {
                var (fileId, bytes) = await _storage.SaveAsync(orgId, patientId, stream, contentType, file.FileName, comment, userId, ct);
                await _hashtag.ExtractAndPersistAsync(orgId, "attachment", fileId, comment, 5, ct);
                
                return CreatedAtAction(nameof(Download), new { fileId }, new { fileId, bytes });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(402, new { message = "Se alcanzó la cuota de almacenamiento de su plan." });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Unsupported content type", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = "Tipo de archivo no permitido." });
            }
            catch (UnauthorizedAccessException)
            {
                return StatusCode(402, new { message = "Su organización no tiene derecho de almacenamiento configurado." });
            }
            catch (IOException ex) when (ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(413, new { message = $"El archivo excede el tamaño máximo permitido ({_options.MaxFileSizeMB} MB)." });
            }
        }

        // ===== 3) Download =====
        [HttpGet("attachments/{fileId:guid}/download")]
        public async Task<IActionResult> Download(Guid fileId, CancellationToken ct)
        {
            try
            {
                var (content, ctype, name) = await _storage.OpenReadAsync(fileId, ct);
                return File(content, ctype, fileDownloadName: name);
            }
            catch (FileNotFoundException)
            {
                return NotFound(new { message = "Archivo no encontrado." });
            }
        }

        // ===== 4) Delete =====
        [HttpDelete("attachments/{fileId:guid}")]
        public async Task<IActionResult> Delete(Guid fileId, CancellationToken ct)
        {
            var userId = GetCurrentUserId();
            if (userId is null) return Forbid();

               // Resolver org del request (mismo patrón que List/Upload)
            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);
            var ok = await _storage.SoftDeleteAsync(fileId, orgId, userId, ct);
            
            return ok ? NoContent() : NotFound(new { message = "Archivo no encontrado o ya eliminado." });
        }
    }
}
