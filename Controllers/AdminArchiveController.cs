using System;
using System.Threading;
using System.Threading.Tasks;
using EPApi.Services.Archive;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/admin/archive")]
    [Authorize(Roles = "admin")] // ajusta según tu auth
    public sealed class AdminArchiveController : ControllerBase
    {
        private readonly IArchiveService _archive;
        private readonly string _cs;

        public AdminArchiveController(IArchiveService archive, IConfiguration cfg)
        {
            _archive = archive ?? throw new ArgumentNullException(nameof(archive));
            _cs = cfg.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing Default connection string");
        }

        [HttpPost("run")]
        public async Task<IActionResult> RunNow(CancellationToken ct)
        {
            var (ok, fail) = await _archive.RunOnceAsync(ct);
            return Ok(new { ok, fail, ranAtUtc = DateTime.UtcNow });
        }

        [HttpGet("last-run")]
        public async Task<IActionResult> LastRun(CancellationToken ct)
        {
            const string SQL = @"
SELECT TOP (1) run_id, started_at_utc, finished_at_utc, ok_count, fail_count, last_error
FROM dbo.archive_runs
ORDER BY started_at_utc DESC;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            await using var rd = await cmd.ExecuteReaderAsync(ct);

            if (!await rd.ReadAsync(ct))
            {
                return Ok(new { message = "No hay corridas registradas aún." });
            }

            return Ok(new
            {
                runId = rd.GetGuid(0),
                startedAtUtc = rd.GetDateTime(1),
                finishedAtUtc = rd.IsDBNull(2) ? (DateTime?)null : rd.GetDateTime(2),
                ok = rd.GetInt32(3),
                fail = rd.GetInt32(4),
                lastError = rd.IsDBNull(5) ? null : rd.GetString(5)
            });
        }
    }
}
