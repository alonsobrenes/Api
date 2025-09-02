using System.Security.Claims;
using EPApi.DataAccess;
using EPApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize] // ajusta si necesitas políticas
    public sealed class PatientsController : ControllerBase
    {
        private readonly IPatientRepository _repo;

        public PatientsController(IPatientRepository repo) => _repo = repo;

        // Intenta obtener un userId (int) de los claims
        private int? GetCurrentUserId()
        {
            // comunes: "uid", "sub", NameIdentifier
            var raw = User.FindFirstValue("uid")
                   ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? User.FindFirstValue("sub");
            if (int.TryParse(raw, out var id)) return id;
            return null;
        }

        private bool IsAdmin()
        {
            // Ajusta según tus roles/claims
            if (User.IsInRole("Admin")) return true;
            var role = User.FindFirstValue(ClaimTypes.Role);
            return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
        }

        [HttpGet]
        public async Task<IActionResult> GetList(
          [FromQuery] int page = 1,
          [FromQuery] int pageSize = 25,
          [FromQuery] string? search = null,
          [FromQuery] bool? active = null,
          CancellationToken ct = default)
        {
            var ownerUserId = GetCurrentUserId();
            var isAdmin = IsAdmin();

            // Usamos la nueva firma (con owner y admin)
            var paged = await _repo.GetPagedAsync(page, pageSize, search, active, ownerUserId, isAdmin, ct);

            // Evitar deconstruction para no chocarnos con versiones del compilador
            var items = paged.Items;
            var total = paged.Total;

            return Ok(new { total, page, pageSize, items });
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        {
            var ownerUserId = GetCurrentUserId();
            var isAdmin = IsAdmin();

            var p = await _repo.GetByIdAsync(id, ownerUserId, isAdmin, ct);
            return p is null ? NotFound() : Ok(p);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] PatientCreateDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            try
            {
                var ownerUserId = GetCurrentUserId();

                // Si tu columna created_by_user_id es NOT NULL, valida que exista ownerUserId
                if (ownerUserId is null)
                    return Forbid(); // o BadRequest con mensaje claro

                var id = await _repo.CreateAsync(dto, ownerUserId.Value, ct);
                return CreatedAtAction(nameof(GetById), new { id }, new { id });
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601) // UNIQUE
            {
                return Conflict(new { message = "El número de identificación ya existe.", field = "identificationNumber" });
            }
        }

        [HttpPut("{id:guid}")]
        public async Task<IActionResult> Update(Guid id, [FromBody] PatientUpdateDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            try
            {
                var ownerUserId = GetCurrentUserId();
                var isAdmin = IsAdmin();

                var ok = await _repo.UpdateAsync(id, dto, ownerUserId, isAdmin, ct);
                return ok ? NoContent() : NotFound();
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
                return Conflict(new { message = "El número de identificación ya existe.", field = "identificationNumber" });
            }
        }

        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var ownerUserId = GetCurrentUserId();
            var isAdmin = IsAdmin();

            var ok = await _repo.DeleteAsync(id, ownerUserId, isAdmin, ct);
            return ok ? NoContent() : NotFound();
        }
    }
}
