// Controllers/AttemptReviewsController.cs
using EPApi.DataAccess;
using EPApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using EPApi.Services.Billing;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/attempts")]
    [Authorize]
    public sealed class AttemptReviewsController : ControllerBase
    {
        private readonly IClinicianReviewRepository _repo;
        private readonly IUsageService _usage;
        private readonly BillingRepository _billing;
        public AttemptReviewsController(IClinicianReviewRepository repo, IUsageService usage, BillingRepository billing)
        { _repo = repo; _usage = usage; _billing = billing; }

        private int? GetCurrentUserId()
        {
            var raw = User.FindFirstValue("uid")
                   ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? User.FindFirstValue("sub");
            return int.TryParse(raw, out var id) ? id : (int?)null;
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
            var org = await _billing.GetOrgIdForUserAsync(RequireUserId(), ct);
            if (org is null) throw new InvalidOperationException("Usuario sin organización");
            return org.Value;
        }

        // Crear un intento (útil al terminar el test cuando scoring_mode='clinician')
        [HttpPost]
        public async Task<ActionResult<CreateAttemptResultDto>> Create([FromBody] CreateAttemptInputDto dto, CancellationToken ct)
        {
            if (dto == null || dto.TestId == Guid.Empty) return BadRequest("TestId requerido.");

            var uid = GetCurrentUserId();
            if (uid is null) return Forbid();

            var r = await _repo.CreateAttemptAsync(dto.TestId, dto.PatientId, uid.Value, ct);
            return Ok(r);
        }

        // Traer la revisión (si existe)
        [HttpGet("{attemptId:guid}/review")]
        public async Task<IActionResult> GetReview(Guid attemptId, CancellationToken ct)
        {
            var r = await _repo.GetReviewAsync(attemptId, ct);
            return Ok(new { attemptId, review = r });
        }

        // Guardar borrador/final
        [HttpPost("{attemptId:guid}/review")]
        public async Task<IActionResult> Upsert(Guid attemptId, [FromBody] ReviewUpsertInputDto body, CancellationToken ct)
        {
            if (body == null) return BadRequest("Body requerido.");
            if (body.Scales == null || body.Scales.Count == 0) return BadRequest("scales vacío.");
            // Validación simple de valores
            foreach (var s in body.Scales)
            {
                var v = (s.Value ?? "").Trim().ToUpperInvariant();
                if (v != "0" && v != "1" && v != "2" && v != "X")
                    return BadRequest("value inválido (usar 0|1|2|X)");
            }
            body.ReviewerUserId = GetCurrentUserId().ToString();

            // Si marca final, consume 1 del plan SACKS
            if (body.IsFinal)
            {
                var orgId = await RequireOrgIdAsync(ct);
                //var gate = await _usage.TryConsumeAsync(orgId, "sacks.monthly", 1, ct);
                var gate = await _usage.TryConsumeAsync(orgId, "sacks.monthly", 1,$"review-final:{attemptId}", ct);
                if (!gate.Allowed)
                    return Problem(statusCode: 402, title: "Límite del plan",
                        detail: "Has alcanzado el límite mensual de SACKS para tu plan.");
            }

            var id = await _repo.UpsertReviewAsync(attemptId, body, ct);
            
            return Ok(new { attemptId, reviewId = id, isFinal = body.IsFinal });
        }
    }
}
