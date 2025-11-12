using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/admin/notifications")]
    [Authorize(Policy = "ManageTaxonomy")]
    public sealed class AdminNotificationsController : ControllerBase
    {
        private readonly string _connString;
        private readonly ILogger<AdminNotificationsController> _log;

        public AdminNotificationsController(IConfiguration cfg, ILogger<AdminNotificationsController> log)
        {
            _connString = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing connection string 'Default'.");
            _log = log;
        }

        // ---------------------------
        // POST /api/admin/notifications
        // Crea notificación (borrador o publicada)
        // ---------------------------
        public sealed class CreateDto
        {
            public string title { get; set; } = "";
            public string body { get; set; } = "";
            public string kind { get; set; } = "info";           // info|success|warning|urgent
            public string audience { get; set; } = "all";         // all|org|user|role
            public string? audienceValue { get; set; }            // CSV/JSON (GUIDs de org/ids de user/roles)
            public DateTime? publishedAtUtc { get; set; }         // null => borrador
            public DateTime? expiresAtUtc { get; set; }           // opcional
            public string? actionUrl { get; set; }
            public string? actionLabel { get; set; }
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateDto dto, CancellationToken ct = default)
        {
            ValidateKind(dto.kind);
            ValidateAudience(dto.audience);

            var userId = GetUserIdOrThrow();
            Guid id;

            using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO dbo.notifications (title, body, kind, audience, audience_value, published_at_utc, expires_at_utc, created_by_user_id, action_url, action_label)
OUTPUT inserted.id
VALUES (@title, @body, @kind, @aud, @aval, @pub, @exp, @cuid, @aurl, @albl);";
            cmd.Parameters.Add(new SqlParameter("@title", SqlDbType.NVarChar, 200) { Value = dto.title ?? "" });
            cmd.Parameters.Add(new SqlParameter("@body", SqlDbType.NVarChar) { Value = dto.body ?? "" });
            cmd.Parameters.Add(new SqlParameter("@kind", SqlDbType.NVarChar, 16) { Value = dto.kind });
            cmd.Parameters.Add(new SqlParameter("@aud", SqlDbType.NVarChar, 16) { Value = dto.audience });
            cmd.Parameters.Add(new SqlParameter("@aval", SqlDbType.NVarChar) { Value = (object?)dto.audienceValue ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@pub", SqlDbType.DateTime2) { Value = (object?)dto.publishedAtUtc ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@exp", SqlDbType.DateTime2) { Value = (object?)dto.expiresAtUtc ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@cuid", SqlDbType.Int) { Value = userId });
            cmd.Parameters.Add(new SqlParameter("@aurl", SqlDbType.NVarChar, 500) { Value = (object?)dto.actionUrl ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@albl", SqlDbType.NVarChar, 80) { Value = (object?)dto.actionLabel ?? DBNull.Value });

            var obj = await cmd.ExecuteScalarAsync(ct);
            id = (obj is Guid g) ? g : Guid.Empty;

            return CreatedAtAction(nameof(GetList), new { id }, new { id });
        }

        // ---------------------------
        // GET /api/admin/notifications?activeOnly=&audience=&q=&top=100
        // Lista para admin (publicadas/borradores)
        // ---------------------------
        [HttpGet]
        public async Task<IActionResult> GetList(
            [FromQuery] bool activeOnly = false,
            [FromQuery] string? audience = null,
            [FromQuery] string? q = null,
            [FromQuery] int top = 100,
            CancellationToken ct = default)
        {
            if (top <= 0 || top > 500) top = 100;

            using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            var where = new List<string>();
            if (activeOnly)
                where.Add("(published_at_utc IS NOT NULL AND published_at_utc <= SYSUTCDATETIME() AND (expires_at_utc IS NULL OR expires_at_utc > SYSUTCDATETIME()))");

            if (!string.IsNullOrWhiteSpace(audience))
            {
                ValidateAudience(audience);
                where.Add("audience = @aud");
            }

            if (!string.IsNullOrWhiteSpace(q))
                where.Add("(title LIKE @q OR body LIKE @q)");

            var sql = $@"
SELECT TOP (@top) id, title, body, kind, audience, audience_value, published_at_utc, expires_at_utc, created_by_user_id, created_at_utc, updated_at_utc,action_url, action_label
FROM dbo.notifications
{(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
ORDER BY COALESCE(published_at_utc, created_at_utc) DESC;";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqlParameter("@top", SqlDbType.Int) { Value = top });
            if (where.Any(w => w.Contains("audience = @aud")))
                cmd.Parameters.Add(new SqlParameter("@aud", SqlDbType.NVarChar, 16) { Value = audience! });
            if (where.Any(w => w.Contains("@q")))
                cmd.Parameters.Add(new SqlParameter("@q", SqlDbType.NVarChar, 4000) { Value = $"%{q}%" });

            var list = new List<object>();
            using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new
                {
                    id = rd.GetGuid(0),
                    title = rd.GetString(1),
                    body = rd.GetString(2),
                    kind = rd.GetString(3),
                    audience = rd.GetString(4),
                    audienceValue = rd.IsDBNull(5) ? null : rd.GetString(5),
                    publishedAtUtc = rd.IsDBNull(6) ? (DateTime?)null : rd.GetDateTime(6),
                    expiresAtUtc = rd.IsDBNull(7) ? (DateTime?)null : rd.GetDateTime(7),
                    createdByUserId = rd.IsDBNull(8) ? (int?)null : rd.GetInt32(8),
                    createdAtUtc = rd.GetDateTime(9),
                    updatedAtUtc = rd.IsDBNull(10) ? (DateTime?)null : rd.GetDateTime(10),
                    actionUrl = rd.IsDBNull(11) ? null : rd.GetString(11),
                    actionLabel = rd.IsDBNull(12) ? null : rd.GetString(12)
                });
            }

            return Ok(list);
        }

        // ---------------------------
        // PATCH /api/admin/notifications/{id}
        // Edita campos y/o publica/expira
        // ---------------------------
        public sealed class PatchDto
        {
            public string? title { get; set; }
            public string? body { get; set; }
            public string? kind { get; set; }                 // valida si viene
            public string? audience { get; set; }             // valida si viene
            public string? audienceValue { get; set; }
            public DateTime? publishedAtUtc { get; set; }     // set explícito
            public bool? publishNow { get; set; }             // true => publishedAtUtc = now
            public bool? unpublish { get; set; }              // true => publishedAtUtc = null
            public DateTime? expiresAtUtc { get; set; }       // set explícito
            public bool? expireNow { get; set; }              // true => expiresAtUtc = now
            public string? actionUrl { get; set; }
            public string? actionLabel { get; set; }
        }

        [HttpPatch("{id:guid}")]
        public async Task<IActionResult> Patch(Guid id, [FromBody] PatchDto dto, CancellationToken ct = default)
        {
            if (dto.kind is not null) ValidateKind(dto.kind);
            if (dto.audience is not null) ValidateAudience(dto.audience);

            var sets = new List<string>();
            var ps = new List<SqlParameter>();

            void Add(string column, object? value, SqlDbType type, int? size = null)
            {
                var p = new SqlParameter("@" + column, type) { Value = value ?? DBNull.Value };
                if (size is int s) p.Size = s;
                ps.Add(p);
                sets.Add($"{column} = @{column}");
            }

            if (dto.title is not null) Add("title", dto.title, SqlDbType.NVarChar, 200);
            if (dto.body is not null) Add("body", dto.body, SqlDbType.NVarChar);
            if (dto.kind is not null) Add("kind", dto.kind, SqlDbType.NVarChar, 16);
            if (dto.audience is not null) Add("audience", dto.audience, SqlDbType.NVarChar, 16);
            if (dto.audienceValue is not null) Add("audience_value", dto.audienceValue, SqlDbType.NVarChar);

            if (dto.publishedAtUtc.HasValue) Add("published_at_utc", dto.publishedAtUtc, SqlDbType.DateTime2);
            if (dto.expiresAtUtc.HasValue) Add("expires_at_utc", dto.expiresAtUtc, SqlDbType.DateTime2);

            if (dto.publishNow == true) Add("published_at_utc", DateTime.UtcNow, SqlDbType.DateTime2);
            if (dto.unpublish == true) Add("published_at_utc", null, SqlDbType.DateTime2);
            if (dto.expireNow == true) Add("expires_at_utc", DateTime.UtcNow, SqlDbType.DateTime2);
            if (dto.actionUrl is not null) Add("action_url", dto.actionUrl, SqlDbType.NVarChar, 500);
            if (dto.actionLabel is not null) Add("action_label", dto.actionLabel, SqlDbType.NVarChar, 80);

            // siempre toca updated_at_utc
            Add("updated_at_utc", DateTime.UtcNow, SqlDbType.DateTime2);

            if (sets.Count == 0) return NoContent();

            using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE dbo.notifications SET {string.Join(", ", sets)} WHERE id = @id;";
            cmd.Parameters.AddRange(ps.ToArray());
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 0) return NotFound();
            return NoContent();
        }

        // ===== helpers =====

        private static void ValidateKind(string kind)
        {
            var k = (kind ?? "info").Trim().ToLowerInvariant();
            if (k is not ("info" or "success" or "warning" or "urgent"))
                throw new ArgumentException("Invalid kind. Allowed: info|success|warning|urgent");
        }

        private static void ValidateAudience(string aud)
        {
            var a = (aud ?? "all").Trim().ToLowerInvariant();
            if (a is not ("all" or "org" or "user" or "role"))
                throw new ArgumentException("Invalid audience. Allowed: all|org|user|role");
        }

        private int GetUserIdOrThrow()
        {
            var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("user_id")
                       ?? User.FindFirstValue("uid");
            if (!int.TryParse(idStr, out var id))
                throw new UnauthorizedAccessException("No user id claim.");
            return id;
        }
    }
}
