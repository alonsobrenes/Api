using System.Security.Claims;
using EPApi.DataAccess;
using EPApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize] // requiere login
    public sealed class UsersController : ControllerBase
    {
        private readonly IUserRepository _repo;
        private readonly IWebHostEnvironment _env;

        public UsersController(IUserRepository repo, IWebHostEnvironment env)
        {
            _repo = repo;
            _env = env;
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
            var uploadsRoot = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "uploads", "avatars");
            Directory.CreateDirectory(uploadsRoot);

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

            var fileName = $"{id}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{ext}";
            var fullPath = Path.Combine(uploadsRoot, fileName);

            await using (var stream = System.IO.File.Create(fullPath))
            {
                await file.CopyToAsync(stream, ct);
            }

            // URL pública (relativa)
            var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
            var publicUrl = $"{baseUrl}/uploads/avatars/{fileName}";

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

        // (Opcional) eliminar avatar
        [HttpDelete("me/avatar")]
        public async Task<ActionResult> DeleteAvatar(CancellationToken ct)
        {
            var id = GetUserId();
            var user = await _repo.GetByIdAsync(id, ct);
            if (user is null) return NotFound();

            // borrar archivo físico si existe
            if (!string.IsNullOrWhiteSpace(user.AvatarUrl))
            {
                var path = user.AvatarUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var full = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), path);
                if (System.IO.File.Exists(full))
                {
                    try { System.IO.File.Delete(full); } catch { /* ignore */ }
                }
            }

            await _repo.UpdateAvatarUrlAsync(id, null, ct);
            return NoContent();
        }       
    }
}
