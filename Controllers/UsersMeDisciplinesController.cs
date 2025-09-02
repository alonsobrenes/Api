using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EPApi.DataAccess;
using System.Security.Claims;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/Users/me/disciplines")]
    [Authorize] // cualquier autenticado
    public sealed class UsersMeDisciplinesController : ControllerBase
    {
        private readonly IUserDisciplineRepository _repo;
        public UsersMeDisciplinesController(IUserDisciplineRepository repo) => _repo = repo;

        private int GetUserId()
        {
            // en tu JWT, el NameIdentifier es el id (string->int)
            var sid = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
            return int.TryParse(sid, out var n) ? n : throw new InvalidOperationException("Invalid user id in token.");
        }

        [HttpGet]
        public async Task<IActionResult> GetMine(CancellationToken ct)
        {
            var uid = GetUserId();
            var rows = await _repo.GetMineAsync(uid, ct);
            return Ok(rows.Select(r => new { id = r.Id, code = r.Code, name = r.Name }));
        }

        public sealed class WriteDto { public int[] DisciplineIds { get; set; } = Array.Empty<int>(); }

        [HttpPut]
        public async Task<IActionResult> ReplaceMine([FromBody] WriteDto dto, CancellationToken ct)
        {
            var uid = GetUserId();
            await _repo.ReplaceMineAsync(uid, dto.DisciplineIds ?? Array.Empty<int>(), ct);
            return NoContent();
        }
    }
}
