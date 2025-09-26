using EPApi.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.DataAccess
{
    public sealed class PatientSessionsRepository : IPatientSessionsRepository
    {
        private readonly string _connString;
        private static readonly Regex HashtagRegex =
            new Regex(@"#([\p{L}\p{N}_-]{2,64})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public PatientSessionsRepository(IConfiguration config)
        {
            _connString = config.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
        }

        public async Task<PagedResult<PatientSessionDto>> ListAsync(
            Guid orgId, Guid patientId, int skip, int take, string? q, int? createdByUserId, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            var sql = @"
SELECT COUNT(1)
FROM dbo.patient_sessions
WHERE org_id = @org AND patient_id = @patient AND deleted_at_utc IS NULL
  AND (@q IS NULL OR title LIKE @q OR content_text LIKE @q)
  AND (@createdBy IS NULL OR created_by_user_id = @createdBy);

SELECT id, patient_id, created_by_user_id, title, content_text, ai_tidy_text, ai_opinion_text,
       created_at_utc, updated_at_utc
FROM dbo.patient_sessions
WHERE org_id = @org AND patient_id = @patient AND deleted_at_utc IS NULL
  AND (@q IS NULL OR title LIKE @q OR content_text LIKE @q)
  AND (@createdBy IS NULL OR created_by_user_id = @createdBy)
ORDER BY updated_at_utc DESC, created_at_utc DESC
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@patient", SqlDbType.UniqueIdentifier) { Value = patientId });
            cmd.Parameters.Add(new SqlParameter("@skip", SqlDbType.Int) { Value = Math.Max(0, skip) });
            cmd.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = Math.Max(1, take) });
            var qParam = q is null ? (object)DBNull.Value : $"%{q}%";
            cmd.Parameters.Add(new SqlParameter("@q", SqlDbType.NVarChar, 4000) { Value = qParam });
            cmd.Parameters.Add(new SqlParameter("@createdBy", SqlDbType.Int) { Value = (object?)createdByUserId ?? DBNull.Value });

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            int total = 0;
            if (await reader.ReadAsync(ct))
                total = reader.GetInt32(0);

            var items = new List<PatientSessionDto>(take);
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
                items.Add(MapDto(reader));

            return new PagedResult<PatientSessionDto> { Items = items, Total = total };
        }

        public async Task<PatientSessionDto> GetAsync(Guid orgId, Guid patientId, Guid id, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, patient_id, created_by_user_id, title, content_text, ai_tidy_text, ai_opinion_text,
       created_at_utc, updated_at_utc
FROM dbo.patient_sessions
WHERE org_id = @org AND patient_id = @patient AND id = @id AND deleted_at_utc IS NULL;";
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@patient", SqlDbType.UniqueIdentifier) { Value = patientId });
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                throw new KeyNotFoundException("Session not found");

            return MapDto(r);
        }

        public async Task<PatientSessionDto> CreateAsync(
            Guid orgId, Guid patientId, int createdByUserId, string title, string? content, CancellationToken ct)
        {
            var id = Guid.NewGuid();

            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO dbo.patient_sessions
    (id, org_id, patient_id, created_by_user_id, title, content_text)
VALUES
    (@id, @org, @patient, @userId, @title, @content);";
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@patient", SqlDbType.UniqueIdentifier) { Value = patientId });
                cmd.Parameters.Add(new SqlParameter("@userId", SqlDbType.Int) { Value = createdByUserId });
                cmd.Parameters.Add(new SqlParameter("@title", SqlDbType.NVarChar, 200) { Value = title });
                cmd.Parameters.Add(new SqlParameter("@content", SqlDbType.NVarChar, -1) { Value = (object?)content ?? DBNull.Value });

                await cmd.ExecuteNonQueryAsync(ct);
            }

            return await GetAsync(orgId, patientId, id, ct);
        }

        public async Task<PatientSessionDto> UpdateAsync(
            Guid orgId, Guid patientId, Guid id, string title, string? content, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE dbo.patient_sessions
SET title = @title,
    content_text = @content,
    updated_at_utc = SYSUTCDATETIME()
WHERE org_id = @org AND patient_id = @patient AND id = @id AND deleted_at_utc IS NULL;";
                cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@patient", SqlDbType.UniqueIdentifier) { Value = patientId });
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                cmd.Parameters.Add(new SqlParameter("@title", SqlDbType.NVarChar, 200) { Value = title });
                cmd.Parameters.Add(new SqlParameter("@content", SqlDbType.NVarChar, -1) { Value = (object?)content ?? DBNull.Value });

                var rows = await cmd.ExecuteNonQueryAsync(ct);
                if (rows == 0) throw new KeyNotFoundException("Session not found or deleted");
            }

            return await GetAsync(orgId, patientId, id, ct);
        }

        public async Task SoftDeleteAsync(Guid orgId, Guid patientId, Guid id, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.patient_sessions
SET deleted_at_utc = SYSUTCDATETIME()
WHERE org_id = @org AND patient_id = @patient AND id = @id AND deleted_at_utc IS NULL;";
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@patient", SqlDbType.UniqueIdentifier) { Value = patientId });
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 0) throw new KeyNotFoundException("Session not found or already deleted");
        }

        public async Task<string?> GetRawContentAsync(Guid orgId, Guid patientId, Guid id, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT content_text
FROM dbo.patient_sessions
WHERE org_id = @org AND patient_id = @patient AND id = @id AND deleted_at_utc IS NULL;";
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@patient", SqlDbType.UniqueIdentifier) { Value = patientId });
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });

            var res = await cmd.ExecuteScalarAsync(ct);
            return res is DBNull ? null : (string?)res;
        }

        public async Task<PatientSessionDto> UpdateAiTidyAsync(
            Guid orgId, Guid patientId, Guid id, string? aiTidy, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE dbo.patient_sessions
SET ai_tidy_text = @tidy, updated_at_utc = SYSUTCDATETIME()
WHERE org_id = @org AND patient_id = @patient AND id = @id AND deleted_at_utc IS NULL;";
                cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@patient", SqlDbType.UniqueIdentifier) { Value = patientId });
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                cmd.Parameters.Add(new SqlParameter("@tidy", SqlDbType.NVarChar, -1) { Value = (object?)aiTidy ?? DBNull.Value });

                var rows = await cmd.ExecuteNonQueryAsync(ct);
                if (rows == 0) throw new KeyNotFoundException("Session not found or deleted");
            }

            return await GetAsync(orgId, patientId, id, ct);
        }

        public async Task<PatientSessionDto> UpdateAiOpinionAsync(
            Guid orgId, Guid patientId, Guid id, string? aiOpinion, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE dbo.patient_sessions
SET ai_opinion_text = @op, updated_at_utc = SYSUTCDATETIME()
WHERE org_id = @org AND patient_id = @patient AND id = @id AND deleted_at_utc IS NULL;";
                cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@patient", SqlDbType.UniqueIdentifier) { Value = patientId });
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                cmd.Parameters.Add(new SqlParameter("@op", SqlDbType.NVarChar, -1) { Value = (object?)aiOpinion ?? DBNull.Value });

                var rows = await cmd.ExecuteNonQueryAsync(ct);
                if (rows == 0) throw new KeyNotFoundException("Session not found or deleted");
            }

            return await GetAsync(orgId, patientId, id, ct);
        }

        public async Task UpsertExplicitHashtagsAsync(Guid orgId, Guid sessionId, string text, CancellationToken ct)
        {
            var tags = ExtractTags(text);
            if (tags.Count == 0) return;

            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync(ct);

            foreach (var tag in tags)
            {
                int hashtagId;
                // find
                await using (var find = conn.CreateCommand())
                {
                    find.CommandText = @"SELECT TOP 1 id FROM dbo.hashtags WHERE org_id = @org AND tag = @tag;";
                    find.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                    find.Parameters.Add(new SqlParameter("@tag", SqlDbType.NVarChar, 64) { Value = tag });
                    var res = await find.ExecuteScalarAsync(ct);
                    hashtagId = res is null ? -1 : Convert.ToInt32(res);
                }

                // insert if missing
                if (hashtagId <= 0)
                {
                    await using var ins = conn.CreateCommand();
                    ins.CommandText = @"
INSERT INTO dbo.hashtags(org_id, tag)
OUTPUT INSERTED.id
VALUES (@org, @tag);";
                    ins.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                    ins.Parameters.Add(new SqlParameter("@tag", SqlDbType.NVarChar, 64) { Value = tag });
                    var inserted = await ins.ExecuteScalarAsync(ct);
                    hashtagId = Convert.ToInt32(inserted);
                }

                // avoid duplicate link
                bool existsLink;
                await using (var chk = conn.CreateCommand())
                {
                    chk.CommandText = @"
SELECT 1 FROM dbo.hashtag_links
WHERE org_id = @org AND hashtag_id = @hid AND target_type = N'session' AND target_id_guid = @sid;";
                    chk.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                    chk.Parameters.Add(new SqlParameter("@hid", SqlDbType.Int) { Value = hashtagId });
                    chk.Parameters.Add(new SqlParameter("@sid", SqlDbType.UniqueIdentifier) { Value = sessionId });
                    var r = await chk.ExecuteScalarAsync(ct);
                    existsLink = r != null;
                }

                if (!existsLink)
                {
                    await using var link = conn.CreateCommand();
                    link.CommandText = @"
INSERT INTO dbo.hashtag_links(org_id, hashtag_id, target_type, target_id_guid)
VALUES (@org, @hid, N'session', @sid);";
                    link.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                    link.Parameters.Add(new SqlParameter("@hid", SqlDbType.Int) { Value = hashtagId });
                    link.Parameters.Add(new SqlParameter("@sid", SqlDbType.UniqueIdentifier) { Value = sessionId });
                    await link.ExecuteNonQueryAsync(ct);
                }
            }
        }

        public async Task<string> ExportPlainTextAsync(Guid orgId, Guid patientId, Guid id, CancellationToken ct)
        {
            var dto = await GetAsync(orgId, patientId, id, ct);

            var sb = new StringBuilder();
            sb.AppendLine($"Título: {dto.Title}");
            sb.AppendLine($"Autor (user id): {dto.CreatedByUserId}");
            sb.AppendLine($"Fecha: {dto.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC");
            sb.AppendLine(new string('-', 40));
            sb.AppendLine("Notas de sesión:");
            sb.AppendLine(dto.ContentText ?? "(vacío)");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(dto.AiTidyText))
            {
                sb.AppendLine("— Texto ordenado (IA) —");
                sb.AppendLine(dto.AiTidyText);
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(dto.AiOpinionText))
            {
                sb.AppendLine("— Opinión IA —");
                sb.AppendLine(dto.AiOpinionText);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // --- helpers ---
        private static PatientSessionDto MapDto(SqlDataReader r)
        {
            // columns order must match SELECTs above
            var id = r.GetGuid(0);
            var patientId = r.GetGuid(1);
            var createdBy = r.GetInt32(2);
            var title = r.GetString(3);
            var content = r.IsDBNull(4) ? null : r.GetString(4);
            var tidy = r.IsDBNull(5) ? null : r.GetString(5);
            var opinion = r.IsDBNull(6) ? null : r.GetString(6);
            var created = r.GetDateTime(7);
            var updated = r.GetDateTime(8);

            return new PatientSessionDto(id, patientId, createdBy, title, content, tidy, opinion, created, updated);
        }

        private static List<string> ExtractTags(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in HashtagRegex.Matches(text))
            {
                var tag = m.Groups[1].Value.Trim().ToLowerInvariant();
                if (tag.Length >= 2 && tag.Length <= 64)
                    set.Add(tag);
            }
            return set.ToList();
        }
    }
}
