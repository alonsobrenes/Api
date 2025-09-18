using EPApi.DataAccess;
using EPApi.Services.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace EPApi.Controllers
{
    /// <summary>
    /// Lectura de hashtags por entidad. No muta datos (la extracción/persistencia
    /// se hace desde controladores de negocio mediante IHashtagService).
    /// </summary>
    [ApiController]
    [Route("api/hashtags")]
    [Authorize]
    public sealed class HashtagsController : ControllerBase
    {
        private readonly HashtagsRepository _repo;
        private readonly BillingRepository _billing;

        public HashtagsController(HashtagsRepository repo, BillingRepository billing)
        {
            _repo = repo;
            _billing = billing;
        }

        /// <summary>
        /// GET /api/hashtags/for?type=session&id={guid}
        /// Devuelve { items: [{ tag: 'ansiedad' }, ...] }
        /// </summary>
        [HttpGet("for")]
        public async Task<ActionResult<object>> GetFor([FromQuery] string type, [FromQuery] Guid id, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(type))
                return BadRequest(new { message = "type requerido" });

            var orgId = await RequireOrgIdAsync(ct);
            var tags = await _repo.GetTagsForAsync(orgId, type.Trim(), id, ct);
            var items = tags.Select(t => new { tag = t }).ToArray();
            return Ok(new { items });
        }

        // ---- helpers ----
        private async Task<Guid> RequireOrgIdAsync(CancellationToken ct)
        {
            // 1) claim org_id
            var claim = User.FindFirst("org_id")?.Value;
            if (Guid.TryParse(claim, out var gid)) return gid;

            // 2) resolver por usuario
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

            if (int.TryParse(userIdStr, out var uid))
            {
                var org = await _billing.GetOrgIdForUserAsync(uid, ct);
                if (org.HasValue) return org.Value;
            }

            throw new UnauthorizedAccessException("No org_id");
        }

        // dentro de HashtagsController
        public sealed record SetHashtagsRequest(string Type, Guid Id, List<string> Tags);

        [HttpPost("set")]
        public async Task<ActionResult<object>> Set([FromBody] SetHashtagsRequest body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body?.Type)) return BadRequest(new { message = "type requerido" });
            var orgId = await RequireOrgIdAsync(ct);

            // Normaliza/filtra como hace tu HashtagService
            var rx = new Regex(@"#?([\p{L}\p{N}_-]{2,64})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var clean = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in body!.Tags ?? new List<string>())
            {
                var m = rx.Match((s ?? "").Trim());
                if (m.Success) clean.Add(m.Groups[1].Value.ToLowerInvariant());
            }

            // Upsert + ReplaceLinks
            var ids = new List<int>(clean.Count);
            foreach (var t in clean)
            {
                var id = await _repo.UpsertHashtagAsync(orgId, t, ct);
                ids.Add(id);
            }
            await _repo.ReplaceLinksAsync(orgId, body.Type.Trim(), body.Id, ids, ct);

            var items = clean.OrderBy(x => x).Select(t => new { tag = t }).ToArray();
            return Ok(new { items });
        }

    }
}
