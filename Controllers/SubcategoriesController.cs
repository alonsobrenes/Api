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
    public sealed class SubcategoriesController : ControllerBase
    {
        private readonly ISubcategoryRepository _repo;

        public SubcategoriesController(ISubcategoryRepository repo) => _repo = repo;

        [HttpGet]        
        public async Task<IActionResult> GetList(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? search = null,
            [FromQuery] bool? active = null,
            [FromQuery] int? categoryId = null,
            [FromQuery] int? disciplineId = null,
            CancellationToken ct = default)
        {
            var (items, total) = await _repo.GetPagedAsync(page, pageSize, search, active, categoryId, disciplineId, ct);
            return Ok(new { total, page, pageSize, items });
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id, CancellationToken ct)
        {
            var x = await _repo.GetByIdAsync(id, ct);
            return x is null ? NotFound() : Ok(x);
        }

        [HttpPost]
        [Authorize(Policy = "ManageTaxonomy")]
        public async Task<IActionResult> Create([FromBody] SubcategoryCreateDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            try
            {
                var id = await _repo.CreateAsync(new Subcategory
                {
                    CategoryId = dto.CategoryId,
                    Code = dto.Code,
                    Name = dto.Name,
                    Description = dto.Description,
                    IsActive = dto.IsActive
                }, ct);

                return CreatedAtAction(nameof(GetById), new { id }, new { id });
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
                return Conflict(new { message = "El código ya existe en esta categoría.", field = "code" });
            }
            catch (SqlException ex) when (ex.Number == 547)
            {
                return BadRequest(new { message = "La categoría no existe.", field = "categoryId" });
            }
        }

        [HttpPut("{id:int}")]
        [Authorize(Policy = "ManageTaxonomy")]
        public async Task<IActionResult> Update(int id, [FromBody] SubcategoryUpdateDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            try
            {
                var ok = await _repo.UpdateAsync(id, new Subcategory
                {
                    Name = dto.Name,
                    Description = dto.Description,
                    IsActive = dto.IsActive
                }, ct);

                return ok ? NoContent() : NotFound();
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
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
                return Conflict(new { message = "No se puede eliminar: existen registros relacionados." });
            }
        }
    }
}
