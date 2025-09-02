using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using EPApi.DataAccess;
using EPApi.Models;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Policy = "ReadTaxonomy")]
    public sealed class DisciplinesController : ControllerBase
    {
        private readonly IDisciplineRepository _repo;

        public DisciplinesController(IDisciplineRepository repo) => _repo = repo;

        [HttpGet]
        public async Task<IActionResult> GetList([FromQuery] int page = 1,
                                                 [FromQuery] int pageSize = 20,
                                                 [FromQuery] string? search = null,
                                                 [FromQuery] bool? active = null,
                                                 CancellationToken ct = default)
        {
            var (items, total) = await _repo.GetPagedAsync(page, pageSize, search, active, ct);
            return Ok(new { total, page, pageSize, items });
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id, CancellationToken ct)
        {
            var d = await _repo.GetByIdAsync(id, ct);
            return d is null ? NotFound() : Ok(d);
        }

        [HttpPost]
        [Authorize(Policy = "ManageTaxonomy")]
        public async Task<IActionResult> Create([FromBody] DisciplineCreateDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            try
            {
                var id = await _repo.CreateAsync(new Discipline
                {
                    Code = dto.Code,
                    Name = dto.Name,
                    Description = dto.Description,
                    IsActive = dto.IsActive
                }, ct);

                return CreatedAtAction(nameof(GetById), new { id }, new { id });
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601) // UQ/PK
            {
                return Conflict(new { message = "El código ya existe.", field = "code" });
            }
        }

        [HttpPut("{id:int}")]
        [Authorize(Policy = "ManageTaxonomy")]
        public async Task<IActionResult> Update(int id, [FromBody] DisciplineUpdateDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            try
            {
                var ok = await _repo.UpdateAsync(id, new Discipline
                {
                    Name = dto.Name,
                    Description = dto.Description,
                    IsActive = dto.IsActive
                }, ct);

                return ok ? NoContent() : NotFound();
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
                // por si en el futuro permites cambiar code y choca UQ
                return Conflict(new { message = "Conflicto de unicidad.", field = "code" });
            }
        }

        [HttpDelete("{id:int}")]
        [Authorize(Policy = "ManageTaxonomy")]
        public async Task<IActionResult> Delete(int id, CancellationToken ct)
        {
            var ok = await _repo.DeleteAsync(id, ct);
            return ok ? NoContent() : NotFound();
        }
    }
}
