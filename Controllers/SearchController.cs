// Controllers/SearchController.cs
using EPApi.DataAccess;
using EPApi.Models.Search;
using EPApi.Services.Search;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Security.Claims;
using EPApi.Services.Orgs;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/search")]
    public sealed class SearchController : ControllerBase // usa tu BaseApiController para GetOrgIdOrThrow()
    {
        private readonly ISearchService _svc;
        private readonly ILogger<SearchController> _log;
        private readonly BillingRepository _billing;
        private readonly IOrgAccessService _orgAccess;

        private int RequireUserId()
        {
            var idStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
            if (!int.TryParse(idStr, out var uid)) throw new UnauthorizedAccessException("No user id");
            return uid;
        }

        private async Task<Guid> RequireOrgIdAsync(CancellationToken ct)
        {
            var uid = RequireUserId();
            var org = await _billing.GetOrgIdForUserAsync(uid, ct);
            if (org is null) throw new InvalidOperationException("Usuario sin organización");
            return org.Value;
        }

        public SearchController(ISearchService svc, ILogger<SearchController> log, BillingRepository billing, IOrgAccessService orgAccess)
        {
            _svc = svc;
            _log = log;
            _billing = billing;
            _orgAccess = orgAccess;
        }

        /// <summary>
        /// F4 — Búsqueda unificada (MVP).
        /// Body: { q, types[], labels[], hashtags[], dateFromUtc, dateToUtc, page, pageSize }
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<SearchResponseDto>> Search([FromBody] SearchRequestDto body, CancellationToken ct)
        {
            var orgId = await RequireOrgIdAsync(ct);
            var userId = RequireUserId(); 
            var allowProfessionals = await _orgAccess
            .IsOwnerOfMultiSeatOrgAsync(userId, orgId, ct);
            _log.LogDebug("Search allowProfessionals={Allow}", allowProfessionals);

            var sw = Stopwatch.StartNew();

            var res = await _svc.SearchAsync(orgId, body ?? new SearchRequestDto(), allowProfessionals, ct);

            _log.LogInformation("search done org={Org} q='{Q}' types={Types} total={Total} dur={Ms}ms",
                orgId, body?.Q, body?.Types == null ? 0 : body.Types.Length, res.Total, sw.ElapsedMilliseconds);
            
            return Ok(res);
        }

        /// <summary>
        /// F5 — Autocomplete / Sugerencias (MVP).
        /// GET /api/search/suggest?q=&limit=10
        /// </summary>
        [HttpGet("suggest")]
        public async Task<ActionResult<SearchSuggestResponse>> Suggest([FromQuery] string q, [FromQuery] int limit = 10, CancellationToken ct = default)
        {
            var orgId = await RequireOrgIdAsync(ct);
            var userId = RequireUserId();
            var allowProfessionals = await _orgAccess
            .IsOwnerOfMultiSeatOrgAsync(userId, orgId, ct);
            _log.LogDebug("Suggest allowProfessionals={Allow}", allowProfessionals);

            //if (limit <= 0 || limit > 50) limit = 10;
            //var lim = Math.Clamp(limit ?? 10, 1, 25);
            var lim = Math.Clamp(limit, 1, 25);
            var res = await _svc.SuggestAsync(orgId, q ?? string.Empty, limit, allowProfessionals, ct);
            return Ok(res);
        }

    }
}
