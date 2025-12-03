using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EPApi.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EPApi.Controllers
{
    [Authorize] // o [Authorize(Policy = "ReadClinic")] si quieres algo más específico
    [ApiController]
    [Route("api/core")]
    public class CoreFilesController : ControllerBase
    {
        private readonly IFileStorage _fileStorage;

        public CoreFilesController(IFileStorage fileStorage)
        {
            _fileStorage = fileStorage;
        }

        // GET /api/core/orgs/{orgIdN}/branding/logo.png
        // GET /api/core/lo-que-sea-dentro-de-core/...
        [HttpGet("{**relativePath}")]
        public async Task<IActionResult> Get(string relativePath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return NotFound();

            // En DB guardamos "core/orgs/...", pero el route nos da "orgs/..."
            // Así que nos aseguramos de que empiece con "core/".
            var storagePath = relativePath.StartsWith("core/", StringComparison.OrdinalIgnoreCase)
                ? relativePath
                : $"core/{relativePath}";

            var stream = await _fileStorage.OpenReadAsync(storagePath, ct);
            if (stream is null)
                return NotFound();

            var ext = Path.GetExtension(storagePath).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".pdf" => "application/pdf",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                _ => "application/octet-stream"
            };

            return File(stream, mime);
        }
    }
}
