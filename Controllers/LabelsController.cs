// Controllers/LabelsController.cs
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EPApi.DataAccess;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public sealed class LabelsController : ControllerBase
    {
        private readonly LabelsRepository _repo;
        public LabelsController(LabelsRepository repo) => _repo = repo;

        public sealed class LabelDto
        {
            public int Id { get; set; }
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public string ColorHex { get; set; } = "#000000";
            public bool IsSystem { get; set; }
            public DateTime CreatedAtUtc { get; set; }
        }

        public sealed class CreateLabelInput
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public string ColorHex { get; set; } = "#1E88E5";
            public bool IsSystem { get; set; } = false;
        }

        [HttpGet]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);
            var rows = await _repo.ListAsync(orgId, ct);
            var items = rows.Select(r => new LabelDto
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

        [HttpPost]
        public async Task<IActionResult> Post([FromBody] CreateLabelInput input, CancellationToken ct)
        {
            if (input is null) return BadRequest("payload vacío");
            if (string.IsNullOrWhiteSpace(input.Code) || input.Code.Length > 64) return BadRequest("code inválido");
            if (!Regex.IsMatch(input.Code, @"^[a-z0-9_-]+$", RegexOptions.IgnoreCase)) return BadRequest("code debe ser slug");
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 128) return BadRequest("name inválido");
            if (!Regex.IsMatch(input.ColorHex ?? "", "^#[0-9A-Fa-f]{6}$")) return BadRequest("color inválido");

            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);
            var id = await _repo.CreateAsync(
                orgId,
                input.Code.Trim().ToLowerInvariant(),
                input.Name.Trim(),
                input.ColorHex.ToUpperInvariant(),
                input.IsSystem,
                ct);

            return Created($"/api/labels/{id}", new { id });
        }

        public sealed class UpdateLabelInput
        {
            public string Name { get; set; } = "";
            public string ColorHex { get; set; } = "#1E88E5";
        }

        [HttpPut("{id:int}")]
        public async Task<IActionResult> Put([FromRoute] int id, [FromBody] UpdateLabelInput input, CancellationToken ct)
        {
            if (id <= 0) return BadRequest("id inválido");
            if (input is null) return BadRequest("payload vacío");
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 128) return BadRequest("name inválido");
            if (!Regex.IsMatch(input.ColorHex ?? "", "^#[0-9A-Fa-f]{6}$")) return BadRequest("color inválido");

            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);

            // No permitir editar labels del sistema (defensa suave)
            var row = await _repo.GetByIdAsync(orgId, id, ct);
            if (row is null) return NotFound();
            if (row.IsSystem) return Conflict(new { message = "No se puede editar una etiqueta del sistema." });

            var ok = await _repo.UpdateAsync(orgId, id, input.Name.Trim(), input.ColorHex.ToUpperInvariant(), ct);
            if (!ok) return NotFound();

            return NoContent();
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete([FromRoute] int id, CancellationToken ct)
        {
            if (id <= 0) return BadRequest("id inválido");
            var orgId = Shared.OrgResolver.GetOrgIdOrThrow(Request, User);

            var row = await _repo.GetByIdAsync(orgId, id, ct);
            if (row is null) return NotFound();
            if (row.IsSystem) return Conflict(new { message = "No se puede eliminar una etiqueta del sistema." });

            // Evitar romper FKs: si hay asignaciones, avisar
            var cnt = await _repo.CountAssignmentsAsync(orgId, id, ct);
            if (cnt > 0) return Conflict(new { message = "La etiqueta tiene asignaciones. Primero desasigne antes de eliminar." });

            var ok = await _repo.DeleteAsync(orgId, id, ct);
            if (!ok) return NotFound();

            return NoContent();
        }

    }
}
