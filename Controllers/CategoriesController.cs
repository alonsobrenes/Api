using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using EPApi.DataAccess;
using EPApi.Models;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Policy = "ReadTaxonomy")]
    public sealed class CategoriesController : ControllerBase
    {
        private readonly ICategoryRepository _repo;

        public CategoriesController(ICategoryRepository repo) => _repo = repo;

        [HttpGet]
        public async Task<IActionResult> GetList(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? search = null,
            [FromQuery] bool? active = null,
            [FromQuery] int? disciplineId = null,
            CancellationToken ct = default)
        {
            var (items, total) = await _repo.GetPagedAsync(page, pageSize, search, active, disciplineId, ct);
            return Ok(new { total, page, pageSize, items });
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id, CancellationToken ct)
        {
            var c = await _repo.GetByIdAsync(id, ct);
            return c is null ? NotFound() : Ok(c);
        }

        [HttpPost]
        [Authorize(Policy = "ManageTaxonomy")]
        public async Task<IActionResult> Create([FromBody] CategoryCreateDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            try
            {
                var id = await _repo.CreateAsync(new Category
                {
                    DisciplineId = dto.DisciplineId,
                    Code = dto.Code,
                    Name = dto.Name,
                    Description = dto.Description,
                    IsActive = dto.IsActive
                }, ct);

                return CreatedAtAction(nameof(GetById), new { id }, new { id });
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601) // UNIQUE
            {
                // UQ_categories__discipline_id__code
                return Conflict(new { message = "El código ya existe en esta disciplina.", field = "code" });
            }
            catch (SqlException ex) when (ex.Number == 547) // FK violation
            {
                // FK_categories__disciplines
                return BadRequest(new { message = "La disciplina no existe.", field = "disciplineId" });
            }
        }

        [HttpPut("{id:int}")]
        [Authorize(Policy = "ManageTaxonomy")]
        public async Task<IActionResult> Update(int id, [FromBody] CategoryUpdateDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            try
            {
                var ok = await _repo.UpdateAsync(id, new Category
                {
                    Name = dto.Name,
                    Description = dto.Description,
                    IsActive = dto.IsActive
                }, ct);

                return ok ? NoContent() : NotFound();
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
                // Por si en el futuro permites cambiar 'code' y choca contra UNIQUE
                return Conflict(new { message = "Conflicto de unicidad.", field = "code" });
            }
        }

        [HttpDelete("{id:int}")]
        [Authorize(Policy = "ManageTaxonomy")]
        public async Task<IActionResult> Delete(int id, CancellationToken ct)
        {
            try
            {
                var ok = await _repo.DeleteAsync(id, ct);
                return ok ? NoContent() : NotFound();
            }
            catch (SqlException ex) when (ex.Number == 547)
            {
                // Si más adelante hay subcategories con FK a categories
                return Conflict(new { message = "No se puede eliminar: existen registros relacionados." });
            }
        }
    }
}
