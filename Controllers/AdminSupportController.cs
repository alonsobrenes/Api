using EPApi.DataAccess;
using EPApi.Services;
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
    [Authorize(Policy = "ManageTaxonomy")]    
    [ApiController]
    [Route("api/admin/support")]
    public class AdminSupportController : ControllerBase
    {
        private readonly ISupportRepository _repo;
        private readonly ISimpleNotificationsService _notify;
        private readonly ISupportAttachmentService _attachments;

        public AdminSupportController(ISupportRepository repo, ISimpleNotificationsService notify, ISupportAttachmentService attachments)
        {
            _repo = repo;
            _notify = notify;
            _attachments = attachments;
        }

        int RequireUserId()
        {
            var raw = User.FindFirstValue("uid")
                    ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.FindFirstValue("sub");
            if (int.TryParse(raw, out var id)) return id;
            throw new UnauthorizedAccessException("No user id");
        }

        [HttpGet] // GET /api/admin/support?top=100&status=open&assignedTo=17&userId=12&category=bug&priority=high&from=2025-01-01&to=2025-12-31&q=transcri
        public async Task<IActionResult> List(
            [FromQuery] int top = 100,
            [FromQuery] string? status = null,
            [FromQuery] int? assignedTo = null,
            [FromQuery] int? userId = null,
            [FromQuery] string? category = null,
            [FromQuery] string? priority = null,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] string? q = null,
            CancellationToken ct = default)
        {
            var rows = await _repo.AdminListTicketsAsync(top, status, assignedTo, userId, category, priority, from, to, q, ct);
            var result = rows.Select(r => new {
                id = r.Id,
                userId = r.UserId,
                orgId = r.OrgId,
                subject = r.Subject,
                status = r.Status,
                priority = r.Priority,
                category = r.Category,
                assignedToUserId = r.AssignedToUserId,
                createdAtUtc = r.CreatedAtUtc,
                updatedAtUtc = r.UpdatedAtUtc,
                lastMessageAtUtc = r.LastMessageAtUtc,
                openedBy = r.OpenedBy
            });
            return Ok(result);
        }

        [HttpGet("{id:guid}")] // detalle con TODOS los mensajes (incluye internos)
        public async Task<IActionResult> GetOne(Guid id, CancellationToken ct)
        {
            var t = await _repo.AdminGetTicketWithMessagesAsync(id, ct);
            if (t is null) return NotFound();

            var attachments = await _attachments.GetForTicketAsync(id, ct);

            var result = new
            {
                t.Id,
                t.Subject,
                t.Status,
                t.Priority,
                t.Category,
                t.CreatedAtUtc,
                t.UpdatedAtUtc,
                t.OpenedBy,
                messages = t.Messages.Select(m => new {
                    m.Id,
                    m.SenderUserId,
                    m.Body,
                    m.CreatedAtUtc,
                    m.IsInternal
                }),
                attachments = attachments.Select(a => new {
                    a.Id,
                    a.FileName,
                    uri = $"/api/admin/support/{t.Id}/attachments/{a.Id}",
                    a.MimeType,
                    a.SizeBytes,
                    a.CreatedAtUtc,
                })
            };
            return Ok(result);
        }
        
        [HttpGet("{id:guid}/attachments/{attachmentId:guid}")]
        public async Task<IActionResult> DownloadAttachment(Guid id, Guid attachmentId, CancellationToken ct)
        {
            // Ya estamos bajo [Authorize(Policy = "ManageTaxonomy")] a nivel de controller,
            // así que solo admin/soporte llegan aquí.

            var ticket = await _repo.AdminGetTicketWithMessagesAsync(id, ct);
            if (ticket is null) return NotFound();

            var result = await _attachments.OpenReadAsync(id, attachmentId, ct);
            if (result is null) return NotFound();

            var (info, stream) = result.Value;
            var fileName = string.IsNullOrWhiteSpace(info.FileName) ? "archivo" : info.FileName;
            var mime = string.IsNullOrWhiteSpace(info.MimeType) ? "application/octet-stream" : info.MimeType;

            return File(stream, mime, fileName);
        }


        public sealed class AdminReplyDto
        {
            [Required] public string body { get; set; } = "";
            public bool internalNote { get; set; } = false;
        }

        [HttpPost("{id:guid}/reply")] // responder como admin (nota pública o interna)
        public async Task<IActionResult> Reply(Guid id, [FromBody] AdminReplyDto body, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);
            var adminId = RequireUserId();
            await _repo.AddAdminMessageAsync(id, adminId, body.body.Trim(), body.internalNote, ct);
            if (!body.internalNote)
            {
                var ownerUserId = await _repo.GetTicketOwnerUserIdAsync(id, ct);
                if (ownerUserId.HasValue)
                {
                    string title = "Respuesta a tu ticket de soporte";
                    // 160 chars aprox para el body de la notificación
                    var trimmed = body.body.Length > 160 ? body.body.Substring(0, 157) : body.body;
                    string kind = "info";
                    string actionUrl = $"/app/help?ticket={id}";
                    string actionLabel = "Ver respuesta";

                    await _notify.CreateForUserAsync(ownerUserId.Value, title, trimmed, kind, actionUrl, actionLabel, adminId, ct);
                }
            }
            return NoContent();
        }

        public sealed class AdminPatchDto
        {
            public string? status { get; set; }           // open|in_progress|resolved|closed
            public int? assignedToUserId { get; set; }    // null = desasignar
        }

        [HttpPatch("{id:guid}")] // cambiar estado/asignación
        public async Task<IActionResult> Patch(Guid id, [FromBody] AdminPatchDto dto, CancellationToken ct)
        {
            if (dto.status is null && dto.assignedToUserId is null) return BadRequest(new { error = "Nada para actualizar" });
            await _repo.UpdateTicketAsync(id, dto.status?.Trim(), dto.assignedToUserId, ct);
            return NoContent();
        }

        [HttpDelete("{id:guid}/attachments/{attachmentId:guid}")]
        public async Task<IActionResult> DeleteAttachment(Guid id, Guid attachmentId, CancellationToken ct)
        {
            try
            {
                await _attachments.DeleteAsync(id, attachmentId, ct);
                return NoContent();
            }
            catch (InvalidOperationException ex)
            {
                // El adjunto no existe o no pertenece al ticket
                return NotFound(new { error = ex.Message });
            }
        }

    }
}
