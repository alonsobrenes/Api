using EPApi.DataAccess;
using EPApi.Services;
using EPApi.Services.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/me/support")]
    public class MeSupportController : ControllerBase
    {
        private readonly ISupportRepository _repo;
        private readonly BillingRepository _billing;
        private readonly ISupportAttachmentService _attachments;

        public MeSupportController(ISupportRepository repo, BillingRepository billing, ISupportAttachmentService attachments)
        {
            _repo = repo;
            _billing = billing;
            _attachments = attachments;
        }

        private const int MaxTicketsPerDayPerUser = 5;

        int RequireUserId()
        {
            var raw = User.FindFirstValue("uid")
                    ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.FindFirstValue("sub");
            if (int.TryParse(raw, out var id)) return id;
            throw new UnauthorizedAccessException("No user id");
        }

        public sealed class CreateTicketDto
        {
            [Required, MaxLength(200)]
            public string subject { get; set; } = "";
            [Required]
            public string description { get; set; } = "";
            [MaxLength(50)]
            public string? category { get; set; }   // bug|feature|billing|other
            [MaxLength(20)]
            public string? priority { get; set; }   // low|normal|high
        }

        [HttpPost] // POST /api/me/support
        public async Task<IActionResult> Create([FromBody] CreateTicketDto body, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);
            var uid = RequireUserId();

            // org opcional: si usas billing para mapear usuario→org
            int? orgId = null;
            var orgGuid = await _billing.GetOrgIdForUserAsync(uid, ct); // según tu patrón en otros controllers

            var windowStart = DateTime.UtcNow.AddDays(-1);

            var recentCount = await _repo.CountTicketsCreatedSinceAsync(uid, windowStart, ct);
            if (recentCount >= MaxTicketsPerDayPerUser)
            {
                // 429 Too Many Requests
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    error = "Has creado muchos tickets en poco tiempo. Por favor, intenta más tarde o contáctanos por correo."
                });
            }

            var id = await _repo.CreateTicketAsync(uid, orgId, body.subject.Trim(), body.description.Trim(), body.category?.Trim(), body.priority?.Trim(), ct);
            return Ok(new { id });
        }

        [HttpGet] // GET /api/me/support?top=50&status=open&q=transcribir
        public async Task<IActionResult> GetMine([FromQuery] int top = 50, [FromQuery] string? status = null, [FromQuery] string? q = null, CancellationToken ct = default)
        {
            var uid = RequireUserId();
            var rows = await _repo.GetMyTicketsAsync(uid, top, status, q, ct);
            var result = rows.Select(r => new
            {
                id = r.Id,
                subject = r.Subject,
                status = r.Status,
                priority = r.Priority,
                category = r.Category,
                createdAtUtc = r.CreatedAtUtc,
                updatedAtUtc = r.UpdatedAtUtc,
                lastMessageAtUtc = r.LastMessageAtUtc
            });
            return Ok(result);
        }

        [HttpGet("{id:guid}")] // GET /api/me/support/{id}
        public async Task<IActionResult> GetOne(Guid id, CancellationToken ct)
        {
            var uid = RequireUserId();
            var ticket = await _repo.GetTicketWithMessagesAsync(id, uid, ct);
            if (ticket is null) return NotFound();
            var attachments = await _attachments.GetForTicketAsync(id, ct);

            var result = new
            {
                ticket.Id,
                ticket.Subject,
                ticket.Status,
                ticket.Priority,
                ticket.Category,
                ticket.CreatedAtUtc,
                ticket.UpdatedAtUtc,
                messages = ticket.Messages.Select(m => new {
                    m.Id,
                    m.Body,
                    m.CreatedAtUtc,
                    m.SenderUserId,
                    mine = m.SenderUserId == uid
                }),
                attachments = attachments.Select(a => new {
                    id = a.Id,
                    fileName = a.FileName,
                    uri = a.Uri,
                    mimeType = a.MimeType,
                    sizeBytes = a.SizeBytes,
                    createdAtUtc = a.CreatedAtUtc
                })
            };
            return Ok(result);
        }

        public sealed class AddMessageDto
        {
            [Required]
            public string body { get; set; } = "";
        }

        public sealed class MeTicketStatusDto
        {
            public string? Status { get; set; }
        }

        [HttpPost("{id:guid}/reply")] // POST /api/me/support/{id}/reply
        public async Task<IActionResult> Reply(Guid id, [FromBody] AddMessageDto body, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);
            var uid = RequireUserId();

            var ticket = await _repo.GetTicketWithMessagesAsync(id, uid, ct);
            if (ticket is null) return NotFound();

            await _repo.AddMessageAsync(id, uid, body.body.Trim(), ct);
            return NoContent();
        }

        [HttpPost("{id:guid}/attachments")]
        [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB
        public async Task<IActionResult> UploadAttachment(Guid id, IFormFile file, CancellationToken ct)
        {
            if (file == null)
                return BadRequest(new { error = "Archivo requerido." });

            var uid = RequireUserId();

            // Validar que el ticket le pertenece al usuario (reutiliza repo)
            var ticket = await _repo.GetTicketWithMessagesAsync(id, uid, ct);
            if (ticket is null) return NotFound();
            
            try
            {
                var info = await _attachments.SaveAsync(id, uid, file, ct);
                return Ok(new
                {
                    id = info.Id,
                    fileName = info.FileName,
                    uri = info.Uri,
                    mimeType = info.MimeType,
                    sizeBytes = info.SizeBytes,
                    createdAtUtc = info.CreatedAtUtc                    
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPatch("{id:guid}/status")]
        public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] MeTicketStatusDto dto, CancellationToken ct)
        {
            var uid = RequireUserId();

            var newStatus = dto.Status?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(newStatus))
            {
                return BadRequest(new { error = "Status requerido." });
            }

            // v1: el usuario solo puede cerrar el ticket
            if (newStatus != "closed")
            {
                return BadRequest(new { error = "Solo puedes cambiar el estado a 'closed'." });
            }

            var updated = await _repo.UpdateTicketStatusByOwnerAsync(id, uid, newStatus, ct);
            if (!updated)
            {
                // no existe, no es tuyo, o ya estaba cerrado
                return NotFound(new { error = "Ticket no encontrado o no autorizado." });
            }

            return NoContent();
        }

    }
}
