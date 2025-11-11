using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EPApi.DataAccess;
using System.Data;
using Microsoft.Data.SqlClient;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/utils")]
    [Authorize] // evita que usuarios anónimos saturen el endpoint
    public class UtilsController : ControllerBase
    {
        private readonly ILogger<UtilsController> _log;
        private readonly IConfiguration _cfg;
        private readonly IWebHostEnvironment _env;
        private readonly string _connString;

        public UtilsController(ILogger<UtilsController> log, IConfiguration cfg, IWebHostEnvironment env)
        {
            _log = log;
            _cfg = cfg;
            _env = env;
            _connString = _cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing connection string 'Default'.");
        }

        // --------------------------------------------------------------------
        // 1) Endpoint de micro-upload: medir velocidad de subida
        // --------------------------------------------------------------------
        [HttpPost("ping-upload")]
        [RequestSizeLimit(2_000_000)] // máximo 2 MB
        public async Task<IActionResult> PingUpload(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            long bytesRead = 0;
            await using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms, ct);
            bytesRead = ms.Length;
            sw.Stop();

            _log.LogInformation("PingUpload: {Bytes} bytes en {Ms} ms", bytesRead, sw.ElapsedMilliseconds);

            return Ok(new
            {
                bytesReceived = bytesRead,
                elapsedMs = sw.ElapsedMilliseconds
            });
        }

        // --------------------------------------------------------------------
        // 2) Información del audio de la entrevista (según DB y filesystem)
        // --------------------------------------------------------------------
        [HttpGet("audio-info/{interviewId:guid}")]
        public async Task<IActionResult> GetAudioInfo(Guid interviewId, CancellationToken ct)
        {
            using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT TOP 1 uri, mime_type, duration_ms, created_at_utc
FROM dbo.interview_audio
WHERE interview_id = @iid
ORDER BY created_at_utc DESC;";
            cmd.Parameters.Add(new SqlParameter("@iid", SqlDbType.UniqueIdentifier) { Value = interviewId });

            using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct))
                return Ok(new { hasAudio = false });

            string uri = rd.GetString(0);
            string mime = rd.GetString(1);
            int? durationMs = rd.IsDBNull(2) ? null : rd.GetInt32(2);
            DateTime createdAt = rd.GetDateTime(3);

            // Resolver ruta física como en tus otros controladores
            string root = _cfg.GetValue<string>("Uploads:Root")
                ?? Path.Combine(_env.ContentRootPath, "wwwroot", "uploads");

            string cleaned = uri.Replace('\\', '/');
            if (cleaned.StartsWith("/")) cleaned = cleaned[1..];
            if (cleaned.StartsWith("uploads/")) cleaned = cleaned["uploads/".Length..];
            string absPath = Path.Combine(root, cleaned.Replace('/', Path.DirectorySeparatorChar));

            long sizeBytes = System.IO.File.Exists(absPath)
                ? new FileInfo(absPath).Length
                : 0L;

            return Ok(new
            {
                hasAudio = true,
                uri,
                mimeType = mime,
                sizeBytes,
                durationMs,
                createdAtUtc = createdAt
            });
        }
    }
}
