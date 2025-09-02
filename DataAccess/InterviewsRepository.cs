using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using static EPApi.Controllers.ClinicianInterviewsController;

namespace EPApi.Services
{
    public sealed class InterviewsRepository : IInterviewsRepository
    {
        private readonly string _cs;

        public InterviewsRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                  ?? throw new InvalidOperationException("Missing connection string 'DefaultConnection'");
        }

        // ================ AUDIO =================

        public async Task AddAudioAsync(Guid interviewId, string uri, string mimeType, long? durationMs, CancellationToken ct = default)
        {
            const string sql = @"
INSERT INTO dbo.interview_audio (interview_id, uri, mime_type, duration_ms, created_at_utc)
VALUES (@iid, @uri, @mime, @dur, SYSUTCDATETIME());";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@iid", SqlDbType.UniqueIdentifier) { Value = interviewId });
            cmd.Parameters.Add(new SqlParameter("@uri", SqlDbType.NVarChar, -1) { Value = (object)uri ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@mime", SqlDbType.NVarChar, 200) { Value = (object)mimeType ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@dur", SqlDbType.BigInt) { Value = (object)durationMs ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<(string Uri, string Mime)?> GetLatestAudioAsync(Guid interviewId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT TOP (1) uri, mime_type
FROM dbo.interview_audio
WHERE interview_id = @iid
ORDER BY created_at_utc DESC;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@iid", SqlDbType.UniqueIdentifier) { Value = interviewId });
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                var uri = rd.GetString(0);
                var mime = rd.IsDBNull(1) ? "" : rd.GetString(1);
                return (uri, mime);
            }
            return null;
        }

        // ================ ESTADO ================

        public async Task SetStatusAsync(Guid interviewId, string status, CancellationToken ct = default)
        {
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            const string q = @"SELECT 1
                       FROM sys.columns
                       WHERE object_id = OBJECT_ID('dbo.interviews') AND name = 'updated_at_utc';";
            bool hasUpdated;
            await using (var check = new SqlCommand(q, cn))
            {
                hasUpdated = (await check.ExecuteScalarAsync(ct)) != null;
            }

            string sql = hasUpdated
                ? @"UPDATE dbo.interviews
            SET status = @st, updated_at_utc = SYSUTCDATETIME()
            WHERE id = @iid;"
                : @"UPDATE dbo.interviews
            SET status = @st
            WHERE id = @iid;";

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@st", SqlDbType.NVarChar, 50) { Value = (object)status ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@iid", SqlDbType.UniqueIdentifier) { Value = interviewId });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task UpdateClinicianDiagnosisAsync(Guid interviewId, string? text, bool close, CancellationToken ct = default)
        {
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            // Compatibilidad: si existe updated_at_utc, también la actualizamos
            const string q = @"SELECT 1
                       FROM sys.columns
                       WHERE object_id = OBJECT_ID('dbo.interviews') AND name = 'updated_at_utc';";
            bool hasUpdated;
            await using (var check = new SqlCommand(q, cn))
            {
                hasUpdated = (await check.ExecuteScalarAsync(ct)) != null;
            }

            string sql = hasUpdated ? @"
UPDATE dbo.interviews
SET clinician_diagnosis = @text,
    clinician_diagnosis_updated_at_utc = SYSUTCDATETIME(),
    updated_at_utc = SYSUTCDATETIME(),
    ended_at_utc = CASE WHEN @close = 1 THEN ISNULL(ended_at_utc, SYSUTCDATETIME()) ELSE ended_at_utc END
WHERE id = @iid;" : @"
UPDATE dbo.interviews
SET clinician_diagnosis = @text,
    clinician_diagnosis_updated_at_utc = SYSUTCDATETIME(),
    ended_at_utc = CASE WHEN @close = 1 THEN ISNULL(ended_at_utc, SYSUTCDATETIME()) ELSE ended_at_utc END
WHERE id = @iid;";

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@text", System.Data.SqlDbType.NVarChar, -1) { Value = (object)text ?? System.DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@close", System.Data.SqlDbType.Bit) { Value = close });
            cmd.Parameters.Add(new SqlParameter("@iid", System.Data.SqlDbType.UniqueIdentifier) { Value = interviewId });
            await cmd.ExecuteNonQueryAsync(ct);
        }


        public async Task<FirstInterviewDto?> GetFirstInterviewByPatientAsync(Guid patientId, CancellationToken ct = default)
        {
            const string sql = @"
WITH last_interview AS (
  SELECT TOP (1) i.id, i.started_at_utc, i.status, i.clinician_diagnosis
  FROM dbo.interviews i
  WHERE i.patient_id = @pid
  ORDER BY COALESCE(i.started_at_utc, i.ended_at_utc, '19000101') DESC, i.id DESC
)
SELECT
  li.id,
  li.started_at_utc,
  li.status,
  tr.text    AS transcriptText,
  dr.content AS draftContent,
  li.clinician_diagnosis AS clinicianDiagnosis
FROM last_interview li
OUTER APPLY (
  SELECT TOP (1) t.text
  FROM dbo.interview_transcripts t
  WHERE t.interview_id = li.id
  ORDER BY t.created_at_utc DESC
) tr
OUTER APPLY (
  SELECT TOP (1) d.content
  FROM dbo.interview_ai_drafts d
  WHERE d.interview_id = li.id
  ORDER BY d.created_at_utc DESC
) dr;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddWithValue("@pid", patientId);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                return new FirstInterviewDto(
                    rd.GetGuid(0),                                    // InterviewId
                    rd.IsDBNull(1) ? (DateTime?)null : rd.GetDateTime(1), // StartedAtUtc
                    rd.IsDBNull(2) ? null : rd.GetString(2),          // Status
                    rd.IsDBNull(3) ? null : rd.GetString(3),          // TranscriptText
                    rd.IsDBNull(4) ? null : rd.GetString(4) ,          // DraftContent,         // DraftContent
                    rd.IsDBNull(5) ? null : rd.GetString(5)           // ClinicianDiagnosis
                );
            }
            return null;
        }




        // ============== TRANSCRIPCIÓN ==============

        public async Task SaveTranscriptAsync(Guid interviewId, string? language, string text, string? wordsJson, CancellationToken ct = default)
        {
            const string sql = @"
IF EXISTS (
  SELECT 1
  FROM dbo.interview_transcripts
  WHERE interview_id = @iid
    AND (language = @lang OR (@lang IS NULL AND language IS NULL))
)
BEGIN
  UPDATE dbo.interview_transcripts
     SET text = @txt,
         words_json = @words,
         created_at_utc = SYSUTCDATETIME()   -- usamos este campo como timestamp de última edición
   WHERE interview_id = @iid
     AND (language = @lang OR (@lang IS NULL AND language IS NULL));
END
ELSE
BEGIN
  INSERT INTO dbo.interview_transcripts (interview_id, language, text, words_json, created_at_utc)
  VALUES (@iid, @lang, @txt, @words, SYSUTCDATETIME());
END
";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@iid", SqlDbType.UniqueIdentifier) { Value = interviewId });
            cmd.Parameters.Add(new SqlParameter("@lang", SqlDbType.NVarChar, 10) { Value = (object)language ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@txt", SqlDbType.NVarChar, -1) { Value = (object)text ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@words", SqlDbType.NVarChar, -1) { Value = (object)wordsJson ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<string?> GetLatestTranscriptTextAsync(Guid interviewId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT TOP (1) text
FROM dbo.interview_transcripts
WHERE interview_id = @iid
ORDER BY created_at_utc DESC;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@iid", SqlDbType.UniqueIdentifier) { Value = interviewId });
            var result = await cmd.ExecuteScalarAsync(ct);
            return result as string;
        }

        // ================ PACIENTE ================

        // InterviewsRepository.cs
        public async Task<(Guid PatientId, string? FullName, string? Sex, DateTime? BirthDate)?>
            GetInterviewPatientAsync(Guid interviewId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT TOP (1)
    i.patient_id,
    LTRIM(RTRIM(CONCAT(
        COALESCE(NULLIF(p.first_name, ''), ''),
        CASE WHEN NULLIF(p.last_name1,'') IS NULL THEN '' ELSE ' ' + p.last_name1 END,
        CASE WHEN NULLIF(p.last_name2,'') IS NULL THEN '' ELSE ' ' + p.last_name2 END
    ))) AS full_name,
    p.sex,
    p.date_of_birth
FROM dbo.interviews i
LEFT JOIN dbo.patients p ON p.id = i.patient_id
WHERE i.id = @iid;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddWithValue("@iid", interviewId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);

            if (await rd.ReadAsync(ct))
            {
                return (
                    rd.GetGuid(0),
                    rd.IsDBNull(1) ? null : rd.GetString(1),               // FullName
                    rd.IsDBNull(2) ? null : rd.GetString(2),               // Sex
                    rd.IsDBNull(3) ? (DateTime?)null : rd.GetDateTime(3)   // BirthDate (date_of_birth)
                );
            }
            return null;
        }


        // ================ BORRADORES IA ================

        public async Task SaveDraftAsync(Guid interviewId, string content, string? model, string? promptVersion, CancellationToken ct = default)
        {
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            // Helpers para detectar columnas opcionales
            static async Task<bool> HasColumnAsync(SqlConnection cn, string table, string column, CancellationToken ct2)
            {
                const string q = @"SELECT 1
                           FROM sys.columns
                           WHERE object_id = OBJECT_ID(@tbl) AND name = @col";
                await using var cmd = new SqlCommand(q, cn);
                cmd.Parameters.AddWithValue("@tbl", table);
                cmd.Parameters.AddWithValue("@col", column);
                var o = await cmd.ExecuteScalarAsync(ct2);
                return o != null;
            }

            // Tabla real en tu esquema
            const string table = "dbo.interview_ai_drafts";

            var hasCreatedAt = await HasColumnAsync(cn, table, "created_at_utc", ct);
            var hasCreatedBy = await HasColumnAsync(cn, table, "created_by_user_id", ct);

            // Armado dinámico para respetar columnas presentes
            var cols = "id, interview_id, content, model, prompt_version";
            var vals = "@id, @iid, @content, @model, @pv";

            if (hasCreatedAt)
            {
                cols += ", created_at_utc";
                vals += ", SYSUTCDATETIME()";
            }
            if (hasCreatedBy)
            {
                cols += ", created_by_user_id";
                vals += ", @createdBy";
            }

            var sql = $"INSERT INTO {table} ({cols}) VALUES ({vals});";

            var id = Guid.NewGuid();

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            cmd.Parameters.Add(new SqlParameter("@iid", SqlDbType.UniqueIdentifier) { Value = interviewId });
            cmd.Parameters.Add(new SqlParameter("@content", SqlDbType.NVarChar, -1) { Value = (object)content ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@model", SqlDbType.NVarChar, 100) { Value = (object)model ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@pv", SqlDbType.NVarChar, 50) { Value = (object)promptVersion ?? DBNull.Value });

            if (hasCreatedBy)
            {
                // Si más adelante quieres guardar el usuario real, pasa aquí ese valor desde el controller.
                cmd.Parameters.Add(new SqlParameter("@createdBy", SqlDbType.Int) { Value = DBNull.Value });
            }

            await cmd.ExecuteNonQueryAsync(ct);
        }


        public async Task<Guid> CreateInterviewAsync(Guid patientId, CancellationToken ct = default)
        {
            var id = Guid.NewGuid();

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            // Detectar si existen las columnas opcionales
            static async Task<bool> HasColumnAsync(SqlConnection cn, string table, string column, CancellationToken ct2)
            {
                const string q = @"SELECT 1
                           FROM sys.columns
                           WHERE object_id = OBJECT_ID(@tbl) AND name = @col";
                await using var cmd = new SqlCommand(q, cn);
                cmd.Parameters.AddWithValue("@tbl", table);
                cmd.Parameters.AddWithValue("@col", column);
                var o = await cmd.ExecuteScalarAsync(ct2);
                return o != null;
            }

            var hasCreated = await HasColumnAsync(cn, "dbo.interviews", "created_at_utc", ct);
            var hasUpdated = await HasColumnAsync(cn, "dbo.interviews", "updated_at_utc", ct);

            string sql;
            if (hasCreated && hasUpdated)
            {
                sql = @"
INSERT INTO dbo.interviews (id, patient_id, status, created_at_utc, updated_at_utc)
VALUES (@id, @pid, @st, SYSUTCDATETIME(), SYSUTCDATETIME());";
            }
            else if (hasCreated) // solo created_at_utc
            {
                sql = @"
INSERT INTO dbo.interviews (id, patient_id, status, created_at_utc)
VALUES (@id, @pid, @st, SYSUTCDATETIME());";
            }
            else
            {
                sql = @"
INSERT INTO dbo.interviews (id, patient_id, status)
VALUES (@id, @pid, @st);";
            }

            await using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                cmd.Parameters.Add(new SqlParameter("@pid", SqlDbType.UniqueIdentifier) { Value = patientId });
                cmd.Parameters.Add(new SqlParameter("@st", SqlDbType.NVarChar, 50) { Value = "new" });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return id;
        }


    }
}
