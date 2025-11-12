using EPApi.DataAccess;
using EPApi.Services.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Security.Claims;

namespace EPApi.Controllers
{
    [ApiController]
    [Route("api/me/notifications")]
    [Authorize]
    public sealed class MeNotificationsController : ControllerBase
    {
        private readonly ILogger<MeNotificationsController> _log;
        private readonly IConfiguration _cfg;
        private readonly BillingRepository _billing;
        private readonly string _connString;

        public MeNotificationsController(
            ILogger<MeNotificationsController> log,
            IConfiguration cfg,
            BillingRepository billing)
        {
            _log = log;
            _cfg = cfg;
            _billing = billing;
            _connString = _cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing connection string 'Default'.");
        }

        // ------------------------
        // GET /api/me/notifications?onlyUnread=true
        // ------------------------
        [HttpGet]
        public async Task<IActionResult> GetMine([FromQuery] bool onlyUnread = false, CancellationToken ct = default)
        {
            var userId = GetUserIdOrThrow();
            var role = GetUserRoleOrNull();
            Guid? orgIdGuid = await _billing.GetOrgIdForUserAsync(userId, ct); // ✅ Guid?

            var rows = await QueryActiveWithReceiptsAsync(userId, ct);

            var filtered = rows.Where(r => MatchesAudience(r, orgIdGuid, userId, role));

            if (onlyUnread) filtered = filtered.Where(r => !r.IsRead);

            var list = filtered
              .OrderByDescending(r => r.PublishedAtUtc)
              .Select(r => new {
                  id = r.Id,
                  title = r.Title,
                  body = r.Body,
                  kind = r.Kind,
                  publishedAtUtc = r.PublishedAtUtc,
                  expiresAt_utc = r.ExpiresAtUtc,
                  audience = r.Audience,
                  audienceValue = r.AudienceValue,
                  isRead = r.IsRead,
                  readAtUtc = r.ReadAtUtc,
                  archivedAtUtc = r.ArchivedAtUtc,
                  actionUrl = r.ActionUrl,
                  actionLabel = r.ActionLabel
              })
              .ToList();

            return Ok(list);
        }

        // ------------------------
        // GET /api/me/notifications/unread-count
        // ------------------------
        [HttpGet("unread-count")]
        public async Task<IActionResult> GetUnreadCount(CancellationToken ct = default)
        {
            var userId = GetUserIdOrThrow();
            var role = GetUserRoleOrNull();
            Guid? orgIdGuid = await _billing.GetOrgIdForUserAsync(userId, ct); // ✅ Guid?

            var rows = await QueryActiveWithReceiptsAsync(userId, ct);
            var count = rows
                .Where(r => MatchesAudience(r, orgIdGuid, userId, role))
                .Count(r => !r.IsRead);

            return Ok(new { unread = count });
        }

        // ------------------------
        // POST /api/me/notifications/{id}/read
        // ------------------------
        [HttpPost("{id:guid}/read")]
        public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct = default)
        {
            var userId = GetUserIdOrThrow();
            using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            // Intentar update; si no existe, insert
            var updated = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE dbo.user_notifications
SET is_read = 1, read_at_utc = SYSUTCDATETIME()
WHERE notification_id = @nid AND user_id = @uid;";
                cmd.Parameters.Add(new SqlParameter("@nid", SqlDbType.UniqueIdentifier) { Value = id });
                cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
                updated = await cmd.ExecuteNonQueryAsync(ct);
            }
            if (updated == 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO dbo.user_notifications (notification_id, user_id, is_read, read_at_utc)
VALUES (@nid, @uid, 1, SYSUTCDATETIME());";
                cmd.Parameters.Add(new SqlParameter("@nid", SqlDbType.UniqueIdentifier) { Value = id });
                cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return NoContent();
        }

        // ------------------------
        // POST /api/me/notifications/{id}/archive
        // ------------------------
        [HttpPost("{id:guid}/archive")]
        public async Task<IActionResult> Archive(Guid id, CancellationToken ct = default)
        {
            var userId = GetUserIdOrThrow();
            using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            // Upsert de archivo (si no hay fila, creamos como leída+archivada)
            var updated = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE dbo.user_notifications
SET archived_at_utc = SYSUTCDATETIME(), is_read = 1, read_at_utc = COALESCE(read_at_utc, SYSUTCDATETIME())
WHERE notification_id = @nid AND user_id = @uid;";
                cmd.Parameters.Add(new SqlParameter("@nid", SqlDbType.UniqueIdentifier) { Value = id });
                cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
                updated = await cmd.ExecuteNonQueryAsync(ct);
            }
            if (updated == 0)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO dbo.user_notifications (notification_id, user_id, is_read, read_at_utc, archived_at_utc)
VALUES (@nid, @uid, 1, SYSUTCDATETIME(), SYSUTCDATETIME());";
                cmd.Parameters.Add(new SqlParameter("@nid", SqlDbType.UniqueIdentifier) { Value = id });
                cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return NoContent();
        }

        // ===== Helpers =====

        private int GetUserIdOrThrow()
        {
            // Intenta NameIdentifier (sub) o claim "user_id"
            var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? User.FindFirstValue("user_id")
                       ?? User.FindFirstValue("uid");
            if (!int.TryParse(idStr, out var id))
                throw new UnauthorizedAccessException("No user id claim.");
            return id;
        }

        private string? GetUserRoleOrNull()
        {
            // Tu setup usa RoleClaimType='role' (según tu contexto)
            return User.FindFirstValue(ClaimTypes.Role) ?? User.FindFirstValue("role");
        }

        private sealed class Row
        {
            public Guid Id { get; init; }
            public int? OrgId { get; init; }
            public string Title { get; init; } = "";
            public string Body { get; init; } = "";
            public string Kind { get; init; } = "info";
            public string Audience { get; init; } = "all";
            public string? AudienceValue { get; init; }
            public DateTime PublishedAtUtc { get; init; }
            public DateTime? ExpiresAtUtc { get; init; }
            public bool IsRead { get; init; }
            public DateTime? ReadAtUtc { get; init; }
            public DateTime? ArchivedAtUtc { get; init; }
            public string? ActionUrl { get; init; }
            public string? ActionLabel { get; init; }
        }

        private async Task<List<Row>> QueryActiveWithReceiptsAsync(int userId, CancellationToken ct)
        {
            var list = new List<Row>();
            using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT n.id, n.org_id, n.title, n.body, n.kind, n.audience, n.audience_value,
       n.published_at_utc, n.expires_at_utc,
       CASE WHEN u.notification_id IS NULL THEN CAST(0 AS bit) ELSE u.is_read END AS is_read,
       u.read_at_utc, u.archived_at_utc,
       n.action_url, n.action_label
FROM dbo.v_notifications_active AS n
LEFT JOIN dbo.user_notifications AS u
  ON u.notification_id = n.id AND u.user_id = @uid
WHERE (u.archived_at_utc IS NULL) -- si está archivada, se oculta
";
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });

            using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new Row
                {
                    Id = rd.GetGuid(0),
                    OrgId = rd.IsDBNull(1) ? null : rd.GetInt32(1),
                    Title = rd.GetString(2),
                    Body = rd.GetString(3),
                    Kind = rd.GetString(4),
                    Audience = rd.GetString(5),
                    AudienceValue = rd.IsDBNull(6) ? null : rd.GetString(6),
                    PublishedAtUtc = rd.GetDateTime(7),
                    ExpiresAtUtc = rd.IsDBNull(8) ? null : rd.GetDateTime(8),
                    IsRead = rd.GetBoolean(9),
                    ReadAtUtc = rd.IsDBNull(10) ? null : rd.GetDateTime(10),
                    ArchivedAtUtc = rd.IsDBNull(11) ? null : rd.GetDateTime(11),
                    ActionUrl = rd.IsDBNull(12) ? null : rd.GetString(12),
                    ActionLabel = rd.IsDBNull(13) ? null : rd.GetString(13),
                });
            }
            return list;
        }

        private static bool MatchesAudience(Row r, Guid? orgIdGuid, int userId, string? role)
        {
            var a = (r.Audience ?? "all").Trim().ToLowerInvariant();
            switch (a)
            {
                case "all":
                    return true;

                case "org":
                    // ✅ Usamos audience_value como lista de GUIDs de organización
                    // Ejemplos válidos: "guid1,guid2" | "guid1;guid2" | '["guid1","guid2"]'
                    if (!orgIdGuid.HasValue || string.IsNullOrWhiteSpace(r.AudienceValue))
                        return false;
                    var orgTokens = SplitTokens(r.AudienceValue);
                    // comparamos en formato "D" (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)
                    var my = orgIdGuid.Value.ToString("D").ToLowerInvariant();
                    return orgTokens.Any(tok =>
                        string.Equals(tok, my, StringComparison.OrdinalIgnoreCase));

                case "user":
                    if (string.IsNullOrWhiteSpace(r.AudienceValue)) return false;
                    var userTokens = SplitTokens(r.AudienceValue);
                    return userTokens.Contains(userId.ToString());

                case "role":
                    if (string.IsNullOrWhiteSpace(r.AudienceValue) || string.IsNullOrWhiteSpace(role)) return false;
                    var roles = SplitTokens(r.AudienceValue).Select(x => x.ToLowerInvariant()).ToHashSet();
                    return roles.Contains(role.ToLowerInvariant());

                default:
                    return false;
            }
        }

        private static HashSet<string> SplitTokens(string raw)
        {
            var s = raw.Trim();
            if (s.StartsWith("[") && s.EndsWith("]"))
                s = s.Substring(1, s.Length - 2);

            var parts = s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(x => x.Trim().Trim('"', '\'', ' '))
                         .Where(p => p.Length > 0)
                         .Select(p => p.ToLowerInvariant());

            return new HashSet<string>(parts);
        }
    }
}
