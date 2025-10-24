using EPApi.DataAccess;
using EPApi.Models;
using EPApi.Services.Orgs;
using EPApi.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/labels")]
    [Authorize]
    public sealed class LabelAssignmentsController : ControllerBase
    {
        private readonly LabelAssignmentsRepository _repo;
        private readonly IOrgAccessService _orgAccess;

        public LabelAssignmentsController(LabelAssignmentsRepository repo,
                                          IOrgAccessService orgAccess) { 
            _repo = repo;
            _orgAccess = orgAccess;
        }

        private int RequireUserId()
        {
            var idStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
            if (!int.TryParse(idStr, out var uid)) throw new UnauthorizedAccessException("No user id");
            return uid;
        }

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
            
            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);

            if (Guid.TryParse(input.TargetId, out var tid))
            {
                var valid = await _repo.ValidateAsync(orgId, input.LabelId, targetType, tid, ct);

                if (!valid) return NotFound(); 

                await _repo.AssignAsync(orgId, input.LabelId, targetType, tid, ct);
            }

            if (int.TryParse(input.TargetId, out var uid))
            {
                var valid = await _repo.ValidateAsync(orgId, input.LabelId, targetType, uid, ct);

                if (!valid) return NotFound();

                await _repo.AssignIntAsync(orgId, input.LabelId, targetType, uid, ct);
            }


            return NoContent();
        }

        [HttpPost("unassign")]
        public async Task<IActionResult> Unassign([FromBody] AssignInput input, CancellationToken ct)
        {
            if (input is null) return BadRequest("payload vacío");
            if (input.LabelId <= 0) return BadRequest("labelId inválido");

            var targetType = (input.TargetType ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(targetType)) return BadRequest("targetType requerido");
           
            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);

            if (Guid.TryParse(input.TargetId, out var tid)) {
                var valid = await _repo.ValidateAsync(orgId, input.LabelId, targetType, tid, ct);
                if (!valid) return NotFound();

                await _repo.UnassignAsync(orgId, input.LabelId, targetType, tid, ct);
            }

            if (int.TryParse(input.TargetId, out var uid)) {
                var valid = await _repo.ValidateAsync(orgId, input.LabelId, targetType, uid, ct);
                if (!valid) return NotFound();

                await _repo.UnassignIntAsync(orgId, input.LabelId, targetType, uid, ct);
            }

            return NoContent();
        }

        [HttpGet("for")]
        public async Task<IActionResult> GetFor([FromQuery] string type, [FromQuery] string id, CancellationToken ct)
        {
            var targetType = (type ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(targetType)) return BadRequest("type requerido");

            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);
            var userId = RequireUserId();
            var okType = SupportedEntityTypes.IsSupported(targetType);
            
            if (!okType) return BadRequest("type no soportado");

            var isOwner = await _orgAccess
            .IsOwnerOfMultiSeatOrgAsync(userId, orgId, ct);


            if (Guid.TryParse(id, out var gid))
            {
                var rows = await _repo.ListForTargetAsync(orgId, type, gid, isOwner, ct);
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
            if (int.TryParse(id, out var iid))
            {
                var rows = await _repo.ListForTargetIntAsync(orgId, type, iid, isOwner, ct);
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

            return BadRequest(new { message = "El parámetro 'id' debe ser GUID o INT válido." });
        }

    }
}
