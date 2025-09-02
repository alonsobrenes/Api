using EPApi.DataAccess;
using EPApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using static EPApi.Models.TestCrudDto;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/tests")]
    [Authorize] // Requiere estar autenticado; las políticas finas van por acción
    public sealed class TestsController : ControllerBase
    {
        private readonly ITestRepository _repo;
        public TestsController(ITestRepository repo) => _repo = repo;

        // ====== Gestión (admin) ======

        [HttpGet]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> GetList([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null, CancellationToken ct = default)
        {
            var (items, total) = await _repo.GetPagedAsync(page, pageSize, search, ct);
            return Ok(new { total, page, pageSize, items });
        }

        [HttpGet("{id:guid}")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        {
            var t = await _repo.GetByIdAsync(id, ct);
            return t is null ? NotFound() : Ok(t);
        }

        [HttpGet("{id:guid}/questions")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> GetQuestions(Guid id, CancellationToken ct)
        {
            var rows = await _repo.GetQuestionsAsync(id, ct);
            return Ok(rows);
        }

        [HttpGet("{id:guid}/scales")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> GetScales(Guid id, CancellationToken ct)
        {
            var rows = await _repo.GetScalesAsync(id, ct);
            return Ok(rows);
        }

        [HttpPost]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> Create([FromBody] TestCreateDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            try
            {
                var id = await _repo.CreateAsync(dto, ct);
                return CreatedAtAction(nameof(GetById), new { id }, new { id });
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601) // UNIQUE
            {
                return Conflict(new { message = "El código de test ya existe.", field = "code" });
            }
            catch (SqlException ex) when (ex.Number == 547) // FK age_group
            {
                return BadRequest(new { message = "El grupo de edad no existe.", field = "ageGroupId" });
            }
        }

        [HttpPut("{id:guid}")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> Update(Guid id, [FromBody] TestUpdateDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            try
            {
                var ok = await _repo.UpdateAsync(id, dto, ct);
                return ok ? NoContent() : NotFound();
            }
            catch (SqlException ex) when (ex.Number == 547) // FK age_group
            {
                return BadRequest(new { message = "El grupo de edad no existe.", field = "ageGroupId" });
            }
        }

        [HttpDelete("{id:guid}")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var ok = await _repo.DeleteAsync(id, ct);
            return ok ? NoContent() : NotFound();
        }

        [HttpPost("{id:guid}/questions")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> CreateQuestion(Guid id, [FromBody] TestQuestionCreateDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);
            try
            {
                var qid = await _repo.CreateQuestionAsync(id, dto, ct);
                return CreatedAtAction(nameof(GetQuestions), new { id }, new { id = qid });
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
                return Conflict(new { message = "El código de pregunta ya existe para este test.", field = "code" });
            }
        }

        [HttpPut("{id:guid}/questions/{qid:guid}")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> UpdateQuestion(Guid id, Guid qid, [FromBody] TestQuestionUpdateDto dto, CancellationToken ct)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);
            var ok = await _repo.UpdateQuestionAsync(id, qid, dto, ct);
            return ok ? NoContent() : NotFound();
        }

        [HttpDelete("{id:guid}/questions/{qid:guid}")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> DeleteQuestion(Guid id, Guid qid, CancellationToken ct)
        {
            var ok = await _repo.DeleteQuestionAsync(id, qid, ct);
            return ok ? NoContent() : NotFound();
        }

        [HttpGet("{id:guid}/question-options")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> GetQuestionOptions(Guid id, CancellationToken ct)
        {
            var rows = await _repo.GetQuestionOptionsByTestAsync(id, ct);
            return Ok(rows);
        }

        [HttpGet("{id:guid}/disciplines")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> GetDisciplines(Guid id, CancellationToken ct)
        {
            var dto = await _repo.GetDisciplinesAsync(id, ct);
            return Ok(dto);
        }

        [HttpPut("{id:guid}/disciplines")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> ReplaceDisciplines(Guid id, [FromBody] TestDisciplinesWriteDto dto, CancellationToken ct)
        {
            await _repo.ReplaceDisciplinesAsync(id, dto.DisciplineIds ?? Array.Empty<int>(), ct);
            return NoContent();
        }

        [HttpGet("{id:guid}/taxonomy")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> GetTaxonomy(Guid id, CancellationToken ct)
        {
            var rows = await _repo.GetTaxonomyAsync(id, ct);
            return Ok(rows.Select(x => new {
                disciplineId = x.DisciplineId,
                disciplineCode = x.DisciplineCode,
                disciplineName = x.DisciplineName,
                categoryId = x.CategoryId,
                categoryCode = x.CategoryCode,
                categoryName = x.CategoryName,
                subcategoryId = x.SubcategoryId,
                subcategoryCode = x.SubcategoryCode,
                subcategoryName = x.SubcategoryName
            }));
        }

        [HttpPut("{id:guid}/taxonomy")]
        [Authorize(Policy = "ManageTests")]
        public async Task<IActionResult> ReplaceTaxonomy(Guid id, [FromBody] TestTaxonomyWriteDto dto, CancellationToken ct)
        {
            await _repo.ReplaceTaxonomyAsync(id, dto.Items ?? Array.Empty<TestTaxonomyWriteItem>(), ct);
            return NoContent();
        }

        // ====== Clínica (autenticado, no requiere ser admin) ======

        [HttpGet("for-me")]
        [Authorize]
        public async Task<IActionResult> GetForMe(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 24,
            [FromQuery] string? search = null,

            // camelCase
            [FromQuery] int? disciplineId = null,
            [FromQuery] string? disciplineCode = null,
            [FromQuery] int? categoryId = null,
            [FromQuery] string? categoryCode = null,
            [FromQuery] int? subcategoryId = null,
            [FromQuery] string? subcategoryCode = null,

            // snake_case (compat)
            [FromQuery(Name = "discipline_id")] int? discipline_id = null,
            [FromQuery(Name = "discipline_code")] string? discipline_code = null,
            [FromQuery(Name = "category_id")] int? category_id = null,
            [FromQuery(Name = "category_code")] string? category_code = null,
            [FromQuery(Name = "subcategory_id")] int? subcategory_id = null,
            [FromQuery(Name = "subcategory_code")] string? subcategory_code = null,

            CancellationToken ct = default)
        {
            var userIdStr = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdStr, out var userId)) return Forbid();

            var filters = new TestsForMeFilters
            {
                DisciplineId = disciplineId ?? discipline_id,
                DisciplineCode = disciplineCode ?? discipline_code,
                CategoryId = categoryId ?? category_id,
                CategoryCode = categoryCode ?? category_code,
                SubcategoryId = subcategoryId ?? subcategory_id,
                SubcategoryCode = subcategoryCode ?? subcategory_code
            };

            var (items, total) = await _repo.GetForUserAsync(userId, page, pageSize, search, filters, ct);
            return Ok(new { total, page, pageSize, items });
        }

        // === NUEVO: endpoints "run" para clínica (sin policy admin) ===

        [HttpGet("{id:guid}/questions-run")]
        public async Task<IActionResult> GetQuestionsRun(Guid id, CancellationToken ct)
        {
            // TODO (opcional): validar que el usuario pueda acceder a este test (asignado o en for-me)
            var rows = await _repo.GetQuestionsAsync(id, ct);
            return Ok(rows);
        }

        [HttpGet("{id:guid}/question-options-run")]
        public async Task<IActionResult> GetQuestionOptionsRun(Guid id, CancellationToken ct)
        {
            var rows = await _repo.GetQuestionOptionsByTestAsync(id, ct);
            return Ok(rows);
        }
    }
}
