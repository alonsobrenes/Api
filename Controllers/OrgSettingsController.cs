using EPApi.DataAccess;
using EPApi.Services.Orgs;
using EPApi.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Controllers
{
    public sealed class OrgSettingsDto
    {
        public string? LogoUrl { get; set; }
    }

    [Authorize] // cualquier autenticado de la org puede leer settings
    [ApiController]
    [Route("api/orgs/settings")]
    public sealed class OrgSettingsController : ControllerBase
    {
        private readonly IOrgRepository _orgRepository;
        private readonly IFileStorage _fileStorage;
        private readonly IOrgAccessService _orgAccess;

        public OrgSettingsController(IOrgRepository orgRepository, IFileStorage fileStorage, IOrgAccessService orgAccess)
        {
            _orgRepository = orgRepository;
            _fileStorage = fileStorage;
            _orgAccess = orgAccess;
        }

        [HttpGet]
        public async Task<ActionResult<OrgSettingsDto>> Get(CancellationToken ct)
        {
            if (!TryGetOrgId(out var orgId)) return BadRequest(new { error = "Missing x-org-id header" });

            var logoUrl = await _orgRepository.GetLogoUrlAsync(orgId, ct);

            return new OrgSettingsDto
            {
                LogoUrl = logoUrl
            };
        }

        [HttpPost("logo")]
        public async Task<IActionResult> UploadLogo([FromForm] IFormFile file, CancellationToken ct)
        {
            if (!TryGetOrgId(out var orgId))
                return BadRequest(new { error = "Missing x-org-id header" });

            if (!await IsAuthorizedOwnerAsync(orgId, ct))
                return Forbid();

            if (file == null || file.Length == 0)
                return BadRequest(new { error = "No file uploaded" });

            // Validar tipo (simple: solo imágenes)
            if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Only image files are allowed as logo." });

            // Opcional: limitar tamaño, ej. 2 MB
            if (file.Length > 2 * 1024 * 1024)
                return BadRequest(new { error = "Logo file is too large (max 2MB)." });

            // Ruta lógica en blob storage
            var relativePath = StoragePathHelper.GetOrgBrandingLogotPath(orgId);
            
            await using var stream = file.OpenReadStream();
            var storedPath = await _fileStorage.SaveAsync(relativePath, stream, ct);

            await _orgRepository.UpdateLogoUrlAsync(orgId, storedPath, ct);

            return Ok(new { logoUrl = storedPath });
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
    }
}
