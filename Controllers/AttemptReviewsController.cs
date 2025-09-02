// Controllers/AttemptReviewsController.cs
using EPApi.DataAccess;
using EPApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/attempts")]
    [Authorize]
    public sealed class AttemptReviewsController : ControllerBase
    {
        private readonly IClinicianReviewRepository _repo;
        public AttemptReviewsController(IClinicianReviewRepository repo) => _repo = repo;

        // Crear un intento (útil al terminar el test cuando scoring_mode='clinician')
        [HttpPost]
        public async Task<ActionResult<CreateAttemptResultDto>> Create([FromBody] CreateAttemptInputDto dto, CancellationToken ct)
        {
            if (dto == null || dto.TestId == Guid.Empty) return BadRequest("TestId requerido.");
            var r = await _repo.CreateAttemptAsync(dto.TestId, dto.PatientId, ct);
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

            var id = await _repo.UpsertReviewAsync(attemptId, body, ct);
            return Ok(new { attemptId, reviewId = id, isFinal = body.IsFinal });
        }
    }
}
