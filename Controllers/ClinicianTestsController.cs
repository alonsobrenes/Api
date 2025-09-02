// Controllers/ClinicianTestsController.cs
using EPApi.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/clinician/tests")]
    [Authorize] // auth normal; NO exige ManageTests
    public sealed class ClinicianTestsController : ControllerBase
    {
        private readonly IClinicianReviewRepository _repo;       
        public ClinicianTestsController(
            IClinicianReviewRepository repo)
        {
            _repo = repo;
        }

        private bool IsAdmin()
        {
            if (User.IsInRole("Admin")) return true;
            var role = User.FindFirstValue(ClaimTypes.Role);
            return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
        }

        private int? GetCurrentUserId()
        {
            var raw = User.FindFirstValue("uid")
                   ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? User.FindFirstValue("sub");
            return int.TryParse(raw, out var id) ? id : null;
        }

        [HttpGet("{testId:guid}/scales-with-items")]
        public async Task<IActionResult> GetScalesWithItems(Guid testId, CancellationToken ct)
        {
            var rows = await _repo.GetScalesWithItemsAsync(testId, ct);
            return Ok(new { testId, scales = rows });
        }
        // GET /api/clinician/tests/top?period=90d&take=5
        // GET /api/clinician/tests/top?period=90d&take=5
        [HttpGet("top")]
        public async Task<IActionResult> GetTop(
            [FromQuery] string? period = "90d",
            [FromQuery] int take = 5,
            CancellationToken ct = default)
        {
            var to = DateTime.UtcNow;
            var from = (period?.EndsWith("d") == true && int.TryParse(period[..^1], out var days))
                ? to.AddDays(-days) : to.AddDays(-90);

            int? userId = null;
            var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (int.TryParse(claim, out var parsed)) userId = parsed;
            var isAdmin = User.IsInRole("Admin") || User.IsInRole("Owner") || User.IsInRole("Manager");

            var items = await _repo.ListTopTestsAsync(from, to, userId, isAdmin, take, ct);
            return Ok(items);
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetForMeById(Guid id, CancellationToken ct = default)
        {
            // TODO: valida visibilidad del test (por rol, por “asignable”, etc.)
            var dto = await _repo.GetTestForClinicianByIdAsync(id, ct);
            
            if (dto == null) return NotFound();
            return Ok(dto); // { id, code, name, ... }
        }
    }
}
