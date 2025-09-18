using EPApi.DataAccess;
using EPApi.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/labels")]
    [Authorize]
    public sealed class LabelAssignmentsController : ControllerBase
    {
        private readonly LabelAssignmentsRepository _repo;
        public LabelAssignmentsController(LabelAssignmentsRepository repo) => _repo = repo;

        public sealed class AssignInput
        {
            [Required] public int LabelId { get; set; }
            [Required] public string TargetType { get; set; } = ""; // 'patient' | 'test' | 'attempt'
            [Required] public string TargetId { get; set; } = "";   // GUID
        }

        [HttpPost("assign")]
        public async Task<IActionResult> Assign([FromBody] AssignInput input, CancellationToken ct)
        {
            if (input is null) return BadRequest("payload vacío");
            if (input.LabelId <= 0) return BadRequest("labelId inválido");

            var targetType = (input.TargetType ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(targetType)) return BadRequest("targetType requerido");
            if (!Guid.TryParse(input.TargetId, out var tid)) return BadRequest("targetId inválido");

            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);

            var valid = await _repo.ValidateAsync(orgId, input.LabelId, targetType, tid, ct);
            if (!valid) return NotFound(); // etiqueta o target no existen / no pertenecen a la org

            await _repo.AssignAsync(orgId, input.LabelId, targetType, tid, ct);
            return NoContent();
        }

        [HttpPost("unassign")]
        public async Task<IActionResult> Unassign([FromBody] AssignInput input, CancellationToken ct)
        {
            if (input is null) return BadRequest("payload vacío");
            if (input.LabelId <= 0) return BadRequest("labelId inválido");

            var targetType = (input.TargetType ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(targetType)) return BadRequest("targetType requerido");
            if (!Guid.TryParse(input.TargetId, out var tid)) return BadRequest("targetId inválido");

            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);

            // Idempotente: si no existe, devolvemos 204 igual, pero si label/target no son válidos, 404
            var valid = await _repo.ValidateAsync(orgId, input.LabelId, targetType, tid, ct);
            if (!valid) return NotFound();

            await _repo.UnassignAsync(orgId, input.LabelId, targetType, tid, ct);
            return NoContent();
        }

        [HttpGet("for")]
        public async Task<IActionResult> GetFor([FromQuery] string type, [FromQuery] string id, CancellationToken ct)
        {
            var targetType = (type ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(targetType)) return BadRequest("type requerido");
            if (!Guid.TryParse(id, out var tid)) return BadRequest("id inválido");

            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);

            // Reusar validación (etiqueta no aplica; validamos target y tipo solamente):
            // Si quieres evitar un SELECT extra, puedes saltarte esto, pero es útil para 404 temprano.
            //var okType = targetType is "patient" or "test" or "attempt" or "attachment" or "session";
            var okType = SupportedEntityTypes.IsSupported(targetType);
            if (!okType) return BadRequest("type no soportado");

            var rows = await _repo.ListForTargetAsync(orgId, targetType, tid, ct);
            var items = rows.Select(r => new LabelsController.LabelDto
            {
                Id = r.Id,
                Code = r.Code,
                Name = r.Name,
                ColorHex = r.ColorHex,
                IsSystem = r.IsSystem,
                CreatedAtUtc = r.CreatedAtUtc
            });
            return Ok(new { items });
        }

    }
}
