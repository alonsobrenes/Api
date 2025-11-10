// Controllers/OrgsBillingController.cs
using EPApi.DataAccess;
using EPApi.Models;
using EPApi.Services.Orgs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace EPApi.Controllers;

[ApiController]
[Route("api/orgs/billing-profile")]
[Authorize] // requiere usuario autenticado
public sealed class OrgsBillingController : ControllerBase
{
    private readonly IOrgBillingProfileRepository _repo;
    private readonly IOrgAccessService _orgAccess;

    public OrgsBillingController(IOrgBillingProfileRepository repo, IOrgAccessService orgAccess)
    {
        _repo = repo;
        _orgAccess = orgAccess;
    }

    private bool TryGetOrgId(out Guid orgId)
    {
        orgId = default;
        if (!Request.Headers.TryGetValue("x-org-id", out var values)) return false;
        var s = values.FirstOrDefault();
        return Guid.TryParse(s, out orgId);
    }

    private int? GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) // si lo mapearas a NameIdentifier
                  ?? User.FindFirstValue(ClaimTypes.Name)        // a veces se usa "name"
                  ?? User.FindFirstValue("sub");                 // JWT "sub"
        // En tu API normalmente guardas el userId (int) en un claim dedicado. Ajusta si usas otro.
        if (int.TryParse(sub, out var id)) return id;

        // Fallback: a veces tienes un claim "uid"
        var uid = User.FindFirstValue("uid");
        if (int.TryParse(uid, out id)) return id;

        return null;
    }

    private static bool IsOwnerRole(ClaimsPrincipal user)
    {
        var role = user.FindFirstValue(ClaimTypes.Role) ?? user.FindFirstValue("role");
        return string.Equals(role, "editor", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsAuthorizedOwnerAsync(Guid orgId, CancellationToken ct)
    {
        if (IsOwnerRole(User)) return true;

        var userId = GetCurrentUserId();
        if (userId == null) return false;

        return await _orgAccess.IsOwnerAsync(userId.Value, ct);
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!TryGetOrgId(out var orgId)) return BadRequest(new { error = "Missing x-org-id header" });

        if (!await IsAuthorizedOwnerAsync(orgId, ct))
            return Forbid();

        var dto = await _repo.GetAsync(orgId, ct);
        if (dto is null) return NotFound(); // FE ya maneja 404 como “perfil vacío”
        return Ok(dto);
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] BillingProfileDto body, CancellationToken ct)
    {
        if (!TryGetOrgId(out var orgId)) return BadRequest(new { error = "Missing x-org-id header" });

        if (!await IsAuthorizedOwnerAsync(orgId, ct))
            return Forbid();

        // Validaciones mínimas espejo del FE
        if (string.IsNullOrWhiteSpace(body.LegalName)) return BadRequest(new { error = "legalName required" });
        if (string.IsNullOrWhiteSpace(body.TaxId)) return BadRequest(new { error = "taxId required" });
        if (string.IsNullOrWhiteSpace(body.ContactEmail)) return BadRequest(new { error = "contactEmail required" });
        if (body.BillingAddress is null) return BadRequest(new { error = "billingAddress required" });
        if (string.IsNullOrWhiteSpace(body.BillingAddress.Line1) ||
            string.IsNullOrWhiteSpace(body.BillingAddress.City) ||
            string.IsNullOrWhiteSpace(body.BillingAddress.PostalCode) ||
            string.IsNullOrWhiteSpace(body.BillingAddress.CountryIso2))
        {
            return BadRequest(new { error = "billingAddress incomplete" });
        }

        await _repo.UpsertAsync(orgId, body, ct);
        return NoContent();
    }
}
