using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EPApi.DataAccess;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Policy = "ReadTaxonomy")]
    public sealed class AgeGroupsController : ControllerBase
    {
        private readonly IAgeGroupRepository _repo;

        public AgeGroupsController(IAgeGroupRepository repo) => _repo = repo;

        // GET /api/AgeGroups?includeInactive=false
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] bool includeInactive = false,
            CancellationToken ct = default)
        {
            var rows = await _repo.GetAllAsync(includeInactive, ct);
            return Ok(rows);
        }

        // GET /api/AgeGroups/{id}
        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetOne(Guid id, CancellationToken ct = default)
        {
            var row = await _repo.GetByIdAsync(id, ct);
            return row is null ? NotFound() : Ok(row);
        }
    }
}
