using EPApi.DataAccess;
using EPApi.Models;
using EPApi.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize] // requiere login
    public sealed class UsersController : ControllerBase
    {
        private readonly IUserRepository _repo;
        private readonly IWebHostEnvironment _env;
        private readonly IFileStorage _fileStorage;

        public UsersController(IUserRepository repo, IWebHostEnvironment env, IFileStorage fileStorage)
        {
            _repo = repo;
            _env = env;
            _fileStorage = fileStorage;
        }

        private int GetUserId()
        {
            var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? User.FindFirstValue("sub"); // JWT "sub"
            if (int.TryParse(sub, out var id)) return id;
            throw new InvalidOperationException("Invalid user id claim.");
        }

        [HttpGet("me")]
        public async Task<ActionResult<UserProfileDto>> GetMe(CancellationToken ct)
        {
            var id = GetUserId();
            
            var user = await _repo.GetByIdAsync(id, ct);
            if (user is null) return NotFound();

            return Ok(new UserProfileDto
            {
                Id = user.Id,
                Email = user.Email,
                Role = user.Role,
                CreatedAt = user.CreatedAt,
                AvatarUrl = user.AvatarUrl
            });
        }

        [AllowAnonymous]
        [HttpGet("{id:int}/avatar")]
        public async Task<IActionResult> GetAvatar(int id, CancellationToken ct)
        {
            // 1) Intentar leer desde Blob/Azurite (nuevo esquema)
            var exts = new[] { ".jpg", ".jpeg", ".png", ".webp", ".img" };
            foreach (var ext in exts)
            {
                // Ideal: usar StoragePathHelper
                var storageKey = StoragePathHelper.GetUserAvatarPath(id, ext.TrimStart('.'));
                var stream = await _fileStorage.OpenReadAsync(storageKey, ct);
                if (stream != null)
                {
                    var mime = ext switch
                    {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".png" => "image/png",
                        ".webp" => "image/webp",
                        _ => "application/octet-stream"
                    };
                    return File(stream, mime);
                }
            }

            // 2) Fallback: avatares legacy en /wwwroot/uploads/avatars
            var user = await _repo.GetByIdAsync(id, ct);
            if (user?.AvatarUrl is string url &&
                url.Contains("/uploads/avatars/", StringComparison.OrdinalIgnoreCase))
            {
                var relative = url
                    .Replace($"{Request.Scheme}://{Request.Host}", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .TrimStart('/');
                var root = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
                var fullPath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

                if (System.IO.File.Exists(fullPath))
                {
                    var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                    var mime = ext switch
                    {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".png" => "image/png",
                        ".webp" => "image/webp",
                        _ => "application/octet-stream"
                    };
                    var fs = System.IO.File.OpenRead(fullPath);
                    return File(fs, mime);
                }
            }

            return NotFound();
        }



        // Subir/actualizar avatar (multipart/form-data, image/*)
        [HttpPost("me/avatar")]
        [RequestSizeLimit(5_000_000)] // 5 MB
        public async Task<ActionResult<UserProfileDto>> UploadAvatar([FromForm] IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0) return BadRequest("Archivo vacío.");

            var allowed = new[] { "image/jpeg", "image/png", "image/webp" };
            if (!allowed.Contains(file.ContentType))
                return BadRequest("Formato no soportado. Usa JPG, PNG o WEBP.");

            var id = GetUserId();
            

            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = file.ContentType switch
                {
                    "image/jpeg" => ".jpg",
                    "image/png" => ".png",
                    "image/webp" => ".webp",
                    _ => ".img"
                };
            }

            var extNoDot = ext.TrimStart('.');
            var storageKey = StoragePathHelper.GetUserAvatarPath(id, extNoDot);
            

            await using (var stream = file.OpenReadStream())
            {
                await _fileStorage.SaveAsync(storageKey, stream, ct);
            }

            var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
            var rev = Guid.NewGuid().ToString("N"); // para cache-busting
            var publicUrl = $"/api/users/{id}/avatar?rev={rev}";

            var ok = await _repo.UpdateAvatarUrlAsync(id, publicUrl, ct);
            if (!ok) return Problem("No se pudo actualizar el perfil.");

            var me = await _repo.GetByIdAsync(id, ct);
            if (me is null) return NotFound();

            return Ok(new UserProfileDto
            {
                Id = me.Id,
                Email = me.Email,
                Role = me.Role,
                CreatedAt = me.CreatedAt,
                AvatarUrl = me.AvatarUrl
            });     
        }
        
        [HttpDelete("me/avatar")]
        public async Task<ActionResult> DeleteAvatar(CancellationToken ct)
        {
            var id = GetUserId();
            var user = await _repo.GetByIdAsync(id, ct);
            if (user is null) return NotFound();

            // borrar archivo físico si existe
            if (!string.IsNullOrWhiteSpace(user.AvatarUrl) &&
        user.AvatarUrl.Contains("/uploads/avatars/", StringComparison.OrdinalIgnoreCase))
            {
                var path = user.AvatarUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var full = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), path);
                if (System.IO.File.Exists(full))
                {
                    try { System.IO.File.Delete(full); } catch { /* ignore */ }
                }
            }

            if (!string.IsNullOrWhiteSpace(user.AvatarUrl) &&
        user.AvatarUrl.Contains("rev=", StringComparison.OrdinalIgnoreCase)) {
                var exts = new[] { ".jpg", ".jpeg", ".png", ".webp", ".img" };
                foreach (var ext in exts)
                {
                    var key = $"core/avatars/user/{id}{ext}";
                    await _fileStorage.DeleteAsync(key, ct); // si lo soporta
                }
            }

                await _repo.UpdateAvatarUrlAsync(id, null, ct);
            return NoContent();
        }       
    }
}
