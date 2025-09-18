// DataAccess/ClinicianReviewRepository.cs
using System.Data;
using EPApi.Models;
using Microsoft.Data.SqlClient;

namespace EPApi.DataAccess
{
    public sealed class ClinicianReviewRepository : IClinicianReviewRepository
    {
        private readonly string _cs;
        public ClinicianReviewRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                  ?? throw new InvalidOperationException("Missing Default connection string");
        }

        // --------- Scales with items ----------
        public async Task<IReadOnlyList<ScaleWithItemsDto>> GetScalesWithItemsAsync(Guid testId, CancellationToken ct = default)
        {
            var byId = new Dictionary<Guid, ScaleWithItemsDto>();

            const string sqlScales = @"
SELECT s.id, s.code, s.name, s.parent_scale_id
FROM dbo.test_scales s
WHERE s.test_id = @testId AND s.is_active = 1
ORDER BY s.name;";

            const string sqlItems = @"
SELECT s.id AS scale_id, q.id AS question_id, q.code, q.[text], q.order_no
FROM dbo.test_scales s
JOIN dbo.test_scale_questions tsq ON tsq.scale_id = s.id
JOIN dbo.test_questions q         ON q.id = tsq.question_id
WHERE s.test_id = @testId
ORDER BY s.name, q.order_no;";

            await using (var con = new SqlConnection(_cs))
            {
                await con.OpenAsync(ct);

                // Escalas
                await using (var cmd = new SqlCommand(sqlScales, con))
                {
                    cmd.Parameters.AddWithValue("@testId", testId);
                    using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                    {
                        var id = rd.GetGuid(0);
                        byId[id] = new ScaleWithItemsDto
                        {
                            Id = id,
                            Code = rd.GetString(1),
                            Name = rd.GetString(2),
                            ParentScaleId = rd.IsDBNull(3) ? (Guid?)null : rd.GetGuid(3),
                            Items = new()
                        };
                    }
                }

                // Ítems
                await using (var cmd = new SqlCommand(sqlItems, con))
                {
                    cmd.Parameters.AddWithValue("@testId", testId);
                    using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                    {
                        var sid = rd.GetGuid(0);
                        if (!byId.TryGetValue(sid, out var s)) continue;

                        s.Items.Add(new ScaleItemDto
                        {
                            Id = rd.GetGuid(1),
                            Code = rd.GetString(2),
                            Text = rd.GetString(3),
                            OrderNo = rd.IsDBNull(4) ? 0 : rd.GetInt32(4)
                        });
                    }
                }
            }

            return byId.Values.ToList();
        }

        public async Task<IReadOnlyList<AttemptAnswerRow>> GetAttemptAnswersAsync(
    Guid attemptId, int? ownerUserId, bool isAdmin, CancellationToken ct = default)
        {
            var list = new List<AttemptAnswerRow>();

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);

            const string sql = @"
SELECT 
    q.id                 AS question_id,
    q.code               AS code,
    q.[text]             AS [text],
    q.question_type      AS question_type,
    aa.answer_text       AS answer_text,
    aa.answer_value      AS answer_value,
    aa.answer_values     AS answer_values_json,
    ISNULL(q.order_no,0) AS order_no
FROM dbo.attempt_answers aa
JOIN dbo.test_attempts atp ON atp.id = aa.attempt_id
JOIN dbo.test_questions q  ON q.id = aa.question_id
WHERE atp.id = @attemptId
ORDER BY ISNULL(q.order_no,0), q.code;";

            await using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@attemptId", attemptId);
                using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    list.Add(new AttemptAnswerRow
                    {
                        QuestionId = rd.GetGuid(0),
                        Code = rd.IsDBNull(1) ? "" : rd.GetString(1),
                        Text = rd.IsDBNull(2) ? "" : rd.GetString(2),
                        QuestionType = rd.IsDBNull(3) ? "" : rd.GetString(3),
                        AnswerText = rd.IsDBNull(4) ? null : rd.GetString(4),
                        AnswerValue = rd.IsDBNull(5) ? null : rd.GetString(5),
                        AnswerValuesJson = rd.IsDBNull(6) ? null : rd.GetString(6),
                        OrderNo = rd.IsDBNull(7) ? 0 : rd.GetInt32(7),
                    });
                }
            }

            return list;
        }


        // --------- Attempt ----------
        public async Task<CreateAttemptResultDto> CreateAttemptAsync(Guid testId, Guid? patientId, int assignedByUserId, CancellationToken ct = default)
        {
            const string sql = @"
DECLARE @id UNIQUEIDENTIFIER = NEWID();
INSERT INTO dbo.test_attempts (id, test_id, patient_id, assigned_by_user_id, status, started_at, created_at, updated_at)
VALUES (@id, @testId, @patientId, @assignedBy, N'in_progress', SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());
SELECT @id AS id;";

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@testId", testId);
            cmd.Parameters.AddWithValue("@patientId", (object?)patientId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@assignedBy", assignedByUserId);

            var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            return new CreateAttemptResultDto
            {
                AttemptId = id,
                Status = "in_progress",
                CreatedAt = DateTime.UtcNow
            };
        }

        // --------- Review (GET) ----------
        public async Task<AttemptReviewDto?> GetReviewAsync(Guid attemptId, CancellationToken ct = default)
        {
            const string sqlHeader = @"
SELECT TOP 1 id, attempt_id, reviewer_user_id, is_final, created_at, updated_at
FROM dbo.attempt_reviews
WHERE attempt_id = @attemptId
ORDER BY is_final DESC, updated_at DESC;";

            const string sqlDetailAndSummary = @"
SELECT scale_id, score, is_uncertain, notes
FROM dbo.attempt_review_scales
WHERE review_id = @rid;

SELECT areas_conflicto, interrelacion, estructura,
       estructura_impulsos, estructura_ajuste, estructura_madurez,
       estructura_realidad, estructura_expresion
FROM dbo.attempt_review_summary
WHERE review_id = @rid;";

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);

            Guid? reviewId = null;
            var dto = new AttemptReviewDto();

            // header
            await using (var cmd = new SqlCommand(sqlHeader, con))
            {
                cmd.Parameters.AddWithValue("@attemptId", attemptId);
                using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return null;

                reviewId = rd.GetGuid(0);
                dto = new AttemptReviewDto
                {
                    ReviewId = reviewId.Value,
                    AttemptId = rd.GetGuid(1),
                    ReviewerUserId = rd.IsDBNull(2) ? null : rd.GetValue(2)?.ToString(),
                    IsFinal = rd.GetBoolean(3),
                    CreatedAt = rd.GetDateTime(4),
                    UpdatedAt = rd.GetDateTime(5)
                };
            }

            // details + summary
            await using (var cmd = new SqlCommand(sqlDetailAndSummary, con))
            {
                cmd.Parameters.AddWithValue("@rid", reviewId);
                using var da = await cmd.ExecuteReaderAsync(ct);

                // details
                while (await da.ReadAsync(ct))
                {
                    dto.Scales.Add(new AttemptReviewScaleRow
                    {
                        ScaleId = da.GetGuid(0),
                        Score = da.IsDBNull(1) ? (int?)null : Convert.ToInt32(da.GetValue(1)),
                        IsUncertain = !da.IsDBNull(2) && da.GetBoolean(2),
                        Notes = da.IsDBNull(3) ? null : da.GetString(3)
                    });
                }

                // move to summary resultset
                if (await da.NextResultAsync(ct) && await da.ReadAsync(ct))
                {
                    dto.AreasConflicto = da.IsDBNull(0) ? null : da.GetString(0);
                    dto.Interrelacion = da.IsDBNull(1) ? null : da.GetString(1);
                    dto.Estructura = da.IsDBNull(2) ? null : da.GetString(2);
                    dto.EstructuraImpulsos = da.IsDBNull(3) ? null : da.GetString(3);
                    dto.EstructuraAjuste = da.IsDBNull(4) ? null : da.GetString(4);
                    dto.EstructuraMadurez = da.IsDBNull(5) ? null : da.GetString(5);
                    dto.EstructuraRealidad = da.IsDBNull(6) ? null : da.GetString(6);
                    dto.EstructuraExpresion = da.IsDBNull(7) ? null : da.GetString(7);
                }
            }

            return dto;
        }

        // --------- Review (UPSERT) ----------
        public async Task<Guid> UpsertReviewAsync(Guid attemptId, ReviewUpsertInputDto body, CancellationToken ct = default)
        {
            // Validación básica aquí:
            if (body.Scales is null || body.Scales.Count == 0)
                throw new ArgumentException("scales vacío");

            foreach (var s in body.Scales)
            {
                var v = (s.Value ?? "").Trim().ToUpperInvariant();
                if (v != "0" && v != "1" && v != "2" && v != "X")
                    throw new ArgumentException("value inválido (usar 0|1|2|X)");
            }

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var tx = await con.BeginTransactionAsync(ct);

            try
            {
                // 1) localizar/crear review (borrador)
                Guid reviewId;

                // Reutiliza la última no-final del mismo intento (ignoramos reviewer para no chocar tipo)
                const string qLastDraft = @"
SELECT TOP 1 id FROM dbo.attempt_reviews
WHERE attempt_id = @attemptId AND is_final = 0
ORDER BY updated_at DESC;";

                await using (var cmd = new SqlCommand(qLastDraft, con, (SqlTransaction)tx))
                {
                    cmd.Parameters.AddWithValue("@attemptId", attemptId);
                    var r = await cmd.ExecuteScalarAsync(ct);
                    if (r is Guid g) reviewId = g;
                    else
                    {
                        const string ins = @"
DECLARE @id UNIQUEIDENTIFIER = NEWID();
INSERT INTO dbo.attempt_reviews (id, attempt_id, reviewer_user_id, is_final, created_at, updated_at)
VALUES(@id, @at, @reviewer_user_id, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
SELECT @id;";
                        using var insCmd = new SqlCommand(ins, con, (SqlTransaction)tx);
                        insCmd.Parameters.AddWithValue("@at", attemptId);
                        insCmd.Parameters.AddWithValue("@reviewer_user_id", body.ReviewerUserId);

                        reviewId = (Guid)(await insCmd.ExecuteScalarAsync(ct))!;
                    }
                }

                // 2) reemplazar detalle
                const string delDetail = "DELETE FROM dbo.attempt_review_scales WHERE review_id = @rid;";
                await using (var delCmd = new SqlCommand(delDetail, con, (SqlTransaction)tx))
                {
                    delCmd.Parameters.AddWithValue("@rid", reviewId);
                    await delCmd.ExecuteNonQueryAsync(ct);
                }

                const string insDetail = @"
INSERT INTO dbo.attempt_review_scales (id, review_id, scale_id, score, is_uncertain, notes)
VALUES (NEWID(), @rid, @sid, @score, @unc, @notes);";

                foreach (var s in body.Scales)
                {
                    var v = (s.Value ?? "").Trim().ToUpperInvariant();
                    int? score = (v == "X") ? (int?)null : int.Parse(v);
                    bool isUnc = (v == "X");

                    await using var cmd = new SqlCommand(insDetail, con, (SqlTransaction)tx);
                    cmd.Parameters.AddWithValue("@rid", reviewId);
                    cmd.Parameters.AddWithValue("@sid", s.ScaleId);
                    cmd.Parameters.AddWithValue("@score", (object?)score ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@unc", isUnc ? 1 : 0);
                    cmd.Parameters.AddWithValue("@notes", (object?)s.Notes ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // 3) upsert summary
                const string upSummary = @"
MERGE dbo.attempt_review_summary AS t
USING (SELECT @rid AS review_id) AS s
ON t.review_id = s.review_id
WHEN MATCHED THEN UPDATE SET
  areas_conflicto       = @areas,
  interrelacion         = @inter,
  estructura            = @estr,
  estructura_impulsos   = @imp,
  estructura_ajuste     = @aj,
  estructura_madurez    = @mad,
  estructura_realidad   = @real,
  estructura_expresion  = @expr
WHEN NOT MATCHED THEN INSERT
  (review_id, areas_conflicto, interrelacion, estructura,
   estructura_impulsos, estructura_ajuste, estructura_madurez,
   estructura_realidad, estructura_expresion)
VALUES (@rid, @areas, @inter, @estr, @imp, @aj, @mad, @real, @expr);";

                await using (var sCmd = new SqlCommand(upSummary, con, (SqlTransaction)tx))
                {
                    sCmd.Parameters.AddWithValue("@rid", reviewId);
                    var sum = body.Summary ?? new ReviewSummaryInputDto();
                    sCmd.Parameters.AddWithValue("@areas", (object?)sum.AreasConflicto ?? DBNull.Value);
                    sCmd.Parameters.AddWithValue("@inter", (object?)sum.Interrelacion ?? DBNull.Value);
                    sCmd.Parameters.AddWithValue("@estr", (object?)sum.Estructura ?? DBNull.Value);
                    sCmd.Parameters.AddWithValue("@imp", (object?)sum.EstructuraImpulsos ?? DBNull.Value);
                    sCmd.Parameters.AddWithValue("@aj", (object?)sum.EstructuraAjuste ?? DBNull.Value);
                    sCmd.Parameters.AddWithValue("@mad", (object?)sum.EstructuraMadurez ?? DBNull.Value);
                    sCmd.Parameters.AddWithValue("@real", (object?)sum.EstructuraRealidad ?? DBNull.Value);
                    sCmd.Parameters.AddWithValue("@expr", (object?)sum.EstructuraExpresion ?? DBNull.Value);
                    await sCmd.ExecuteNonQueryAsync(ct);
                }

                // 4) marcar final/borrador y estado del intento
                if (body.IsFinal)
                {
                    // sólo 1 final por intento
                    const string clearFinals = "UPDATE dbo.attempt_reviews SET is_final = 0 WHERE attempt_id = @aid AND is_final = 1;";
                    await using (var cf = new SqlCommand(clearFinals, con, (SqlTransaction)tx))
                    {
                        cf.Parameters.AddWithValue("@aid", attemptId);
                        await cf.ExecuteNonQueryAsync(ct);
                    }

                    const string setFinal = "UPDATE dbo.attempt_reviews SET is_final = 1, updated_at = SYSUTCDATETIME() WHERE id = @rid;";
                    await using (var sf = new SqlCommand(setFinal, con, (SqlTransaction)tx))
                    {
                        sf.Parameters.AddWithValue("@rid", reviewId);
                        await sf.ExecuteNonQueryAsync(ct);
                    }

                    const string updAttempt = "UPDATE dbo.test_attempts SET status = N'reviewed', updated_at = SYSUTCDATETIME() WHERE id = @aid;";
                    await using (var ua = new SqlCommand(updAttempt, con, (SqlTransaction)tx))
                    {
                        ua.Parameters.AddWithValue("@aid", attemptId);
                        await ua.ExecuteNonQueryAsync(ct);
                    }
                }
                else
                {
                    const string updAttempt = "UPDATE dbo.test_attempts SET status = N'review_pending', updated_at = SYSUTCDATETIME() WHERE id = @aid;";
                    await using var ua = new SqlCommand(updAttempt, con, (SqlTransaction)tx);
                    ua.Parameters.AddWithValue("@aid", attemptId);
                    await ua.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                return reviewId;
            }
            catch
            {
                try { await tx.RollbackAsync(ct); } catch { }
                throw;
            }
        }

        // DataAccess/ClinicianReviewRepository.cs (dentro de la clase existente)

        // DataAccess/ClinicianReviewRepository.cs  (reemplaza el método completo)
        public async Task<IReadOnlyList<PatientAssessmentRow>> ListAssessmentsByPatientAsync(
            Guid patientId, int? ownerUserId, bool isAdmin, CancellationToken ct = default)
        {
            // NOTA: no usamos finished_at porque no existe en tu schema actual.
            const string sql = @"
SELECT
    a.id             AS attempt_id,
    a.patient_id     AS patient_id,
    a.test_id        AS test_id,
    t.code           AS test_code,
    t.name           AS test_name,
    t.scoring_mode   AS scoring_mode,
    a.status         AS status,
    a.started_at     AS started_at,
    a.updated_at     AS updated_at,
    CAST(
      CASE 
        WHEN a.status IN (
            N'auto_done', N'done', N'completed', N'finished', N'reviewed', N'results_ready'
        ) THEN 1
        WHEN EXISTS (
            SELECT 1
            FROM dbo.attempt_reviews r
            WHERE r.attempt_id = a.id AND r.is_final = 1
        ) THEN 1
        ELSE 0
      END
    AS bit)          AS is_final
FROM dbo.test_attempts a
JOIN dbo.tests    t ON t.id = a.test_id
JOIN dbo.patients p ON p.id = a.patient_id
WHERE a.patient_id = @patientId
  AND (@isAdmin = 1 OR p.created_by_user_id = @ownerUserId)
ORDER BY COALESCE(a.updated_at, a.started_at) DESC;";


            var list = new List<PatientAssessmentRow>();

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@patientId", patientId);
            cmd.Parameters.AddWithValue("@isAdmin", isAdmin ? 1 : 0);
            cmd.Parameters.AddWithValue("@ownerUserId", (object?)ownerUserId ?? DBNull.Value);

            using var rd = await cmd.ExecuteReaderAsync(ct);

            var ordAttemptId = rd.GetOrdinal("attempt_id");
            var ordPatientId = rd.GetOrdinal("patient_id");
            var ordTestId = rd.GetOrdinal("test_id");
            var ordTestCode = rd.GetOrdinal("test_code");
            var ordTestName = rd.GetOrdinal("test_name");
            var ordScoring = rd.GetOrdinal("scoring_mode");
            var ordStatus = rd.GetOrdinal("status");
            var ordStartedAt = rd.GetOrdinal("started_at");
            var ordUpdatedAt = rd.GetOrdinal("updated_at");
            var ordIsFinal = rd.GetOrdinal("is_final");

            while (await rd.ReadAsync(ct))
            {
                // status para derivar FinishedAt si ya quedó “auto_done” o “reviewed”
                var statusStr = rd.IsDBNull(ordStatus) ? "" : rd.GetString(ordStatus);
                var updatedAt = rd.IsDBNull(ordUpdatedAt) ? DateTime.UtcNow : rd.GetDateTime(ordUpdatedAt);

                DateTime? finishedAt = (statusStr.Equals("auto_done", StringComparison.OrdinalIgnoreCase)
                                     || statusStr.Equals("reviewed", StringComparison.OrdinalIgnoreCase))
                                     ? updatedAt
                                     : (DateTime?)null;

                list.Add(new PatientAssessmentRow
                {
                    AttemptId = rd.GetGuid(ordAttemptId),
                    PatientId = rd.GetGuid(ordPatientId),
                    TestId = rd.GetGuid(ordTestId),
                    TestCode = rd.IsDBNull(ordTestCode) ? "" : rd.GetString(ordTestCode),
                    TestName = rd.IsDBNull(ordTestName) ? "" : rd.GetString(ordTestName),
                    ScoringMode = rd.IsDBNull(ordScoring) ? null : rd.GetString(ordScoring),
                    Status = statusStr,
                    StartedAt = rd.IsDBNull(ordStartedAt) ? (DateTime?)null : rd.GetDateTime(ordStartedAt),
                    FinishedAt = finishedAt,           // <- derivado si aplica
                    UpdatedAt = updatedAt,            // <- tu DTO lo requiere no-null
                    ReviewFinalized = rd.GetBoolean(ordIsFinal)
                });
            }

            return list;
        }

        public async Task<TestBasicDto?> GetBasicForClinicianByIdAsync(Guid id, CancellationToken ct = default)
        {
            const string sql = @"
SELECT t.id, t.code, t.name, t.pdf_url, t.is_active
FROM dbo.tests t
WHERE t.id = @id;  -- si quieres, aquí puedes filtrar por 'is_active = 1'
";
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                return new TestBasicDto
                {
                    Id = rd.GetGuid(0),
                    Code = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    Name = rd.IsDBNull(2) ? "" : rd.GetString(2),
                    PdfUrl = rd.IsDBNull(3) ? null : rd.GetString(3),
                    IsActive = !rd.IsDBNull(4) && rd.GetBoolean(4),
                };
            }
            return null;
        }

        public async Task<TestBasicDto?> GetTestForClinicianByIdAsync(Guid id, CancellationToken ct = default)
        {
            const string sql = @"
        SELECT id, code, name
        FROM dbo.tests
        WHERE id = @id; -- aquí puedes filtrar por 'is_published', etc.
    ";
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddWithValue("@id", id);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                return new TestBasicDto
                {
                    Id = rd.GetGuid(0),
                    Code = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    Name = rd.IsDBNull(2) ? "" : rd.GetString(2),
                };
            }
            return null;
        }

        // DataAccess/ClinicianReviewRepository.cs  (reemplaza el método)
        public async Task<bool> DeleteAttemptIfDraftAsync(
    Guid attemptId, int? ownerUserId, bool isAdmin, CancellationToken ct = default)
        {
            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var tx = (SqlTransaction)await con.BeginTransactionAsync(ct);

            try
            {
                // --- detectar columna de propietario en dbo.patients ---
                string? ownerCol = null;
                const string qHasCol = @"
SELECT COUNT(*) 
FROM sys.columns c
JOIN sys.tables  t ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = N'dbo' AND t.name = N'patients' AND c.name = @col;";

                async Task<bool> ColExistsAsync(string col)
                {
                    await using var cmd = new SqlCommand(qHasCol, con, tx);
                    cmd.Parameters.AddWithValue("@col", col);
                    var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                    return n > 0;
                }

                if (await ColExistsAsync("owner_user_id")) ownerCol = "owner_user_id";
                else if (await ColExistsAsync("created_by_user_id")) ownerCol = "created_by_user_id";

                // --- localizar intento / dueño (si aplica)
                int? dbOwner = null;
                const string qOwnerTemplate = @"
SELECT {0}
FROM dbo.patients p
JOIN dbo.test_attempts a ON a.patient_id = p.id
WHERE a.id = @aid;";

                if (ownerCol is not null)
                {
                    var qOwner = string.Format(qOwnerTemplate, $"p.{ownerCol}");
                    await using var cmd = new SqlCommand(qOwner, con, tx);
                    cmd.Parameters.AddWithValue("@aid", attemptId);
                    using var rd = await cmd.ExecuteReaderAsync(ct);
                    if (!await rd.ReadAsync(ct)) { await tx.RollbackAsync(ct); return false; }
                    dbOwner = rd.IsDBNull(0) ? (int?)null : rd.GetInt32(0);
                }
                else
                {
                    const string qExists = @"SELECT COUNT(1) FROM dbo.test_attempts WHERE id = @aid;";
                    await using var cmd = new SqlCommand(qExists, con, tx);
                    cmd.Parameters.AddWithValue("@aid", attemptId);
                    var exists = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
                    if (!exists) { await tx.RollbackAsync(ct); return false; }
                }

                // --- autorización
                if (!isAdmin)
                {
                    if (ownerCol is not null)
                    {
                        if (ownerUserId is null || dbOwner != ownerUserId)
                        { await tx.RollbackAsync(ct); return false; }
                    }
                    else
                    {
                        await tx.RollbackAsync(ct);
                        return false;
                    }
                }

                // --- no permitir borrar si hay review final
                const string qHasFinal = @"SELECT COUNT(1) FROM dbo.attempt_reviews WHERE attempt_id = @aid AND is_final = 1;";
                int finals;
                await using (var cmd = new SqlCommand(qHasFinal, con, tx))
                { cmd.Parameters.AddWithValue("@aid", attemptId); finals = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)); }
                if (finals > 0) { await tx.RollbackAsync(ct); return false; }

                // --- sólo borradores
                const string qStatus = @"SELECT status FROM dbo.test_attempts WHERE id = @aid;";
                string? status;
                await using (var cmd = new SqlCommand(qStatus, con, tx))
                { cmd.Parameters.AddWithValue("@aid", attemptId); status = (string?)await cmd.ExecuteScalarAsync(ct); }
                if (string.IsNullOrWhiteSpace(status) ||
                    !(string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(status, "review_pending", StringComparison.OrdinalIgnoreCase)))
                {
                    await tx.RollbackAsync(ct);
                    return false;
                }

                // --- DEBUG previo: cuántas respuestas hay
                const string qCountAns = @"SELECT COUNT(*) FROM dbo.attempt_answers WHERE attempt_id = @aid;";
                int beforeAnswers;
                await using (var c0 = new SqlCommand(qCountAns, con, tx))
                { c0.Parameters.AddWithValue("@aid", attemptId); beforeAnswers = Convert.ToInt32(await c0.ExecuteScalarAsync(ct)); }
                // Opcional: log interno
                // Console.WriteLine($"[DeleteAttempt] attemptId={attemptId} answers(before)={beforeAnswers}");

                // --- borrar review_scales -> summary -> reviews (no finales)
                const string delScales = @"
DELETE s
FROM dbo.attempt_review_scales s
WHERE s.review_id IN (SELECT id FROM dbo.attempt_reviews WHERE attempt_id = @aid);";
                await using (var d1 = new SqlCommand(delScales, con, tx))
                { d1.Parameters.AddWithValue("@aid", attemptId); await d1.ExecuteNonQueryAsync(ct); }

                const string delSummary = @"
DELETE m
FROM dbo.attempt_review_summary m
WHERE m.review_id IN (SELECT id FROM dbo.attempt_reviews WHERE attempt_id = @aid);";
                await using (var d2 = new SqlCommand(delSummary, con, tx))
                { d2.Parameters.AddWithValue("@aid", attemptId); await d2.ExecuteNonQueryAsync(ct); }

                const string delReviews = @"DELETE FROM dbo.attempt_reviews WHERE attempt_id = @aid AND is_final = 0;";
                await using (var d3 = new SqlCommand(delReviews, con, tx))
                { d3.Parameters.AddWithValue("@aid", attemptId); await d3.ExecuteNonQueryAsync(ct); }

                // --- borrar RESPUESTAS (clave para romper el FK)
                const string delAnswers = @"DELETE FROM dbo.attempt_answers WHERE attempt_id = @aid;";
                int delAns;
                await using (var dA = new SqlCommand(delAnswers, con, tx))
                { dA.Parameters.AddWithValue("@aid", attemptId); delAns = await dA.ExecuteNonQueryAsync(ct); }

                // --- DEBUG posterior
                int afterAnswers;
                await using (var c1 = new SqlCommand(qCountAns, con, tx))
                { c1.Parameters.AddWithValue("@aid", attemptId); afterAnswers = Convert.ToInt32(await c1.ExecuteScalarAsync(ct)); }
                // Opcional: log interno
                // Console.WriteLine($"[DeleteAttempt] attemptId={attemptId} answers(deleted)={delAns} answers(after)={afterAnswers}");

                // --- borrar el intento
                const string delAttempt = @"DELETE FROM dbo.test_attempts WHERE id = @aid;";
                int affected;
                await using (var d4 = new SqlCommand(delAttempt, con, tx))
                { d4.Parameters.AddWithValue("@aid", attemptId); affected = await d4.ExecuteNonQueryAsync(ct); }

                await tx.CommitAsync(ct);
                return affected > 0;
            }
            catch
            {
                try { await tx.RollbackAsync(ct); } catch { }
                throw;
            }
        }

        public async Task<AttemptMetaDto?> GetAttemptMetaAsync(Guid attemptId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT id        AS AttemptId,
       test_id   AS TestId,
       patient_id AS PatientId,
       status    AS Status,
       started_at AS StartedAt,
       created_at AS CreatedAt,
       updated_at AS UpdatedAt
FROM dbo.test_attempts
WHERE id = @id;";
            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@id", attemptId);
            using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            return new AttemptMetaDto
            {
                AttemptId = attemptId,
                TestId = rd.GetGuid(1),
                PatientId = rd.IsDBNull(2) ? (Guid?)null : rd.GetGuid(2),
                Status = rd.GetString(3),
                StartedAt = rd.GetDateTime(4),
                CreatedAt = rd.GetDateTime(5),
                UpdatedAt = rd.GetDateTime(6),
            };
        }
        public async Task FinalizeAttemptAsync(Guid attemptId, CancellationToken ct = default)
        {
            const string sql = @"
UPDATE dbo.test_attempts
SET status = N'reviewed',
    updated_at = SYSUTCDATETIME()
WHERE id = @id;";

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@id", attemptId);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            if (n == 0) throw new InvalidOperationException("Attempt no encontrado");
        }

        


        public async Task<IReadOnlyList<PatientListItem>> ListRecentPatientsAsync(
    int? ownerUserId, bool isAdmin, int take, CancellationToken ct = default)
        {
            const string SQL = @"
SELECT TOP (@take)
  p.id, p.first_name, p.last_name1, p.last_name2, p.identification_number
FROM dbo.patients p
WHERE (@isAdmin = 1 OR p.created_by_user_id = @owner)
ORDER BY p.updated_at DESC;";

            var list = new List<PatientListItem>();
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.AddWithValue("@take", take);
            cmd.Parameters.AddWithValue("@isAdmin", isAdmin ? 1 : 0);
            cmd.Parameters.AddWithValue("@owner", (object?)ownerUserId ?? DBNull.Value);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new PatientListItem
                {
                    Id = rd.GetGuid(0),
                    FirstName = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    LastName1 = rd.IsDBNull(2) ? "" : rd.GetString(2),
                    LastName2 = rd.IsDBNull(3) ? "" : rd.GetString(3),
                    IdentificationNumber = rd.IsDBNull(4) ? "" : rd.GetString(4),
                });
            }
            return list;
        }

        public async Task<IReadOnlyList<TestTopItem>> ListTopTestsAsync(
    DateTime fromUtc, DateTime toUtc, int? clinicianUserId, bool isAdmin, int take, CancellationToken ct = default)
        {
            const string SQL = @"
SELECT TOP (@take)
  a.test_id,
  t.code,
  t.name,
  COUNT_BIG(*) AS usageCount
FROM dbo.test_attempts a
JOIN dbo.tests t ON t.id = a.test_id
WHERE a.status IN (N'reviewed', N'auto_done', N'finished')
  AND COALESCE(a.completed_at, a.updated_at, a.started_at, a.created_at) >= @from
  AND COALESCE(a.completed_at, a.updated_at, a.started_at, a.created_at) <  @to
  AND (
        @isAdmin = 1 OR @uid IS NULL OR
        EXISTS (
          SELECT 1
          FROM dbo.patients p
          WHERE p.id = a.patient_id
            AND p.created_by_user_id = @uid
        )
      )
GROUP BY a.test_id, t.code, t.name
ORDER BY usageCount DESC;";

            var list = new List<TestTopItem>();
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.AddWithValue("@from", fromUtc);
            cmd.Parameters.AddWithValue("@to", toUtc);
            cmd.Parameters.AddWithValue("@take", take);
            cmd.Parameters.AddWithValue("@uid", (object?)clinicianUserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@isAdmin", isAdmin ? 1 : 0);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new TestTopItem
                {
                    Id = rd.GetGuid(0),
                    Code = rd.IsDBNull(1) ? "" : rd.GetString(1),
                    Name = rd.IsDBNull(2) ? "" : rd.GetString(2),
                    UsageCount = (int)rd.GetInt64(3)
                });
            }
            return list;
        }






        public async Task<Guid> LogAutoAttemptAsync(Guid testId, Guid? patientId, DateTime? startedAtUtc, int assignedByUserId, CancellationToken ct = default)
        {
            const string sql = @"
DECLARE @id UNIQUEIDENTIFIER = NEWID();
INSERT INTO dbo.test_attempts (id, test_id, patient_id, assigned_by_user_id, status, started_at, created_at, updated_at)
VALUES (@id, @testId, @patientId, @assignedBy, N'auto_done', @startedAt, SYSUTCDATETIME(), SYSUTCDATETIME());
SELECT @id;";

            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@testId", testId);
            cmd.Parameters.AddWithValue("@patientId", (object?)patientId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@assignedBy", assignedByUserId);
            cmd.Parameters.AddWithValue("@startedAt", (object?)(startedAtUtc ?? DateTime.UtcNow) ?? DBNull.Value);

            var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            return id;
        }



        // Guarda (reemplaza) todas las respuestas de un attempt (incluye created_at)
        public async Task UpsertAttemptAnswersAsync(
            Guid attemptId,
            IReadOnlyList<AttemptAnswerWriteDto> answers,
            CancellationToken ct = default)
        {
            await using var con = new SqlConnection(_cs);
            await con.OpenAsync(ct);
            await using var tx = await con.BeginTransactionAsync(ct);

            try
            {
                const string del = @"DELETE FROM dbo.attempt_answers WHERE attempt_id = @aid;";
                await using (var d = new SqlCommand(del, con, (SqlTransaction)tx))
                {
                    d.Parameters.AddWithValue("@aid", attemptId);
                    await d.ExecuteNonQueryAsync(ct);
                }

                if (answers is { Count: > 0 })
                {
                    const string ins = @"
INSERT INTO dbo.attempt_answers
(id, attempt_id, question_id, answer_text, answer_value, answer_values, created_at)
VALUES (NEWID(), @aid, @qid, @txt, @val, @vals, SYSUTCDATETIME());";

                    foreach (var a in answers)
                    {
                        await using var c = new SqlCommand(ins, con, (SqlTransaction)tx);
                        c.Parameters.AddWithValue("@aid", attemptId);
                        c.Parameters.AddWithValue("@qid", a.QuestionId);
                        c.Parameters.AddWithValue("@txt", (object?)a.Text ?? DBNull.Value);
                        c.Parameters.AddWithValue("@val", (object?)a.Value ?? DBNull.Value);
                        c.Parameters.AddWithValue("@vals", (object?)a.ValuesJson ?? DBNull.Value);
                        await c.ExecuteNonQueryAsync(ct);
                    }
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                try { await tx.RollbackAsync(ct); } catch { }
                throw;
            }
        }


        // Lee respuestas del attempt (primero attempt_answers; si no hay, fallback a test_run_answers)
        public async Task<AttemptSummaryDto> GetAttemptSummaryAsync(
    DateTime fromUtc, DateTime toUtc, int? clinicianUserId, bool isAdmin, CancellationToken ct = default)
        {
            // Usamos test_attempts porque hoy NO se están persistiendo test_runs.
            // Completado = reviewed | auto_done | finished
            const string SQL = @"
SELECT
  COUNT_BIG(*) AS total,
  COUNT_BIG(*) AS finished
FROM dbo.test_attempts a
WHERE a.status IN (N'reviewed', N'auto_done', N'finished')
  AND COALESCE(a.completed_at, a.updated_at, a.started_at, a.created_at) >= @from
  AND COALESCE(a.completed_at, a.updated_at, a.started_at, a.created_at) <  @to
  AND (
       @isAdmin = 1 OR @uid IS NULL OR
        EXISTS (
          SELECT 1
          FROM dbo.patients p
          WHERE p.id = a.patient_id
            AND p.created_by_user_id = @uid
        )
      );";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.AddWithValue("@from", fromUtc);
            cmd.Parameters.AddWithValue("@to", toUtc);
            cmd.Parameters.AddWithValue("@uid", (object?)clinicianUserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@isAdmin", isAdmin ? 1 : 0);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                return new AttemptSummaryDto
                {
                    Total = rd.IsDBNull(0) ? 0 : (int)rd.GetInt64(0),
                    Finished = rd.IsDBNull(1) ? 0 : (int)rd.GetInt64(1),
                };
            }
            return new AttemptSummaryDto();
        }

        public async Task<AiOpinionDto?> GetAiOpinionByAttemptAsync(Guid attemptId, CancellationToken ct = default)
        {
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            const string sql = @"
SELECT TOP (1)
       opinion_text,
       opinion_json,
       risk_level,
       model_version,
       prompt_version,
       input_hash
FROM dbo.test_attempt_ai_opinions
WHERE attempt_id = @aid;";

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@aid", SqlDbType.UniqueIdentifier) { Value = attemptId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct))
                return null;

            var dto = new AiOpinionDto
            {
                OpinionText = rd.IsDBNull(0) ? null : rd.GetString(0),
                OpinionJson = rd.IsDBNull(1) ? null : rd.GetString(1),
                RiskLevel = rd.IsDBNull(2) ? (byte?)null : rd.GetByte(2),
                ModelVersion = rd.IsDBNull(3) ? null : rd.GetString(3),
                PromptVersion = rd.IsDBNull(4) ? null : rd.GetString(4),
                InputHash = rd.IsDBNull(5) ? null : rd.GetString(5)
            };

            return dto;
        }

        public async Task UpsertAiOpinionAsync(Guid attemptId,
  Guid patientId,
  string? text,
  string? json,
  string? model,
  string? promptVersion,
  string? inputHash,
  byte? risk,
  CancellationToken ct = default)
        {
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            const string sql = @"
IF EXISTS (SELECT 1 FROM dbo.test_attempt_ai_opinions WHERE attempt_id=@aid)
BEGIN
  UPDATE dbo.test_attempt_ai_opinions
     SET opinion_text=@txt, opinion_json=@js, model_version=@model, prompt_version=@pver,
         input_hash=@hash, risk_level=@risk, updated_at_utc = SYSUTCDATETIME()
   WHERE attempt_id=@aid;
END
ELSE
BEGIN
  INSERT INTO dbo.test_attempt_ai_opinions
    (attempt_id, patient_id, opinion_text, opinion_json, model_version, prompt_version, input_hash, risk_level, created_at_utc)
  VALUES (@aid, @pid, @txt, @js, @model, @pver, @hash, @risk, SYSUTCDATETIME());
END";

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@aid", System.Data.SqlDbType.UniqueIdentifier) { Value = attemptId });
            cmd.Parameters.Add(new SqlParameter("@pid", System.Data.SqlDbType.UniqueIdentifier) { Value = patientId });
            cmd.Parameters.Add(new SqlParameter("@txt", System.Data.SqlDbType.NVarChar, -1) { Value = (object?)text ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@js", System.Data.SqlDbType.NVarChar, -1) { Value = (object?)json ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@model", System.Data.SqlDbType.NVarChar, 100) { Value = (object?)model ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@pver", System.Data.SqlDbType.NVarChar, 50) { Value = (object?)promptVersion ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@hash", System.Data.SqlDbType.NVarChar, 100) { Value = (object?)inputHash ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@risk", System.Data.SqlDbType.TinyInt) { Value = (object?)risk ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<IReadOnlyList<TestBasicDto>> GetTestsForClinicianAsync(int userId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT DISTINCT t.id, t.code, t.name, t.pdf_url, t.is_active
FROM dbo.tests t
JOIN dbo.test_disciplines td ON td.test_id = t.id
JOIN dbo.user_disciplines ud ON ud.discipline_id = td.discipline_id AND ud.user_id = @uid
WHERE t.is_active = 1
ORDER BY t.name;";

            var list = new List<TestBasicDto>();
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new TestBasicDto
                {
                    Id = rd.GetGuid(0),
                    Code = rd.GetString(1),
                    Name = rd.GetString(2),
                    PdfUrl = rd.IsDBNull(3) ? null : rd.GetString(3),
                    IsActive = !rd.IsDBNull(4) && rd.GetBoolean(4)
                });
            }
            return list;
        }


        public async Task<AttemptAiBundle?> GetAttemptBundleForAiAsync(Guid attemptId, CancellationToken ct = default)
        {
            // 1) Meta del intento: testId, patientId, testName
            Guid testId;
            Guid patientId;
            string testName;

            const string SQL_META = @"
SELECT a.test_id, a.patient_id, t.name
FROM dbo.test_attempts a
JOIN dbo.tests t ON t.id = a.test_id
WHERE a.id = @aid;";
            await using (var cn = new SqlConnection(_cs))
            {
                await cn.OpenAsync(ct);

                await using (var cmd = new SqlCommand(SQL_META, cn))
                {
                    cmd.Parameters.Add(new SqlParameter("@aid", System.Data.SqlDbType.UniqueIdentifier) { Value = attemptId });
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    if (!await rd.ReadAsync(ct)) return null;

                    testId = rd.GetGuid(0);
                    patientId = rd.IsDBNull(1) ? Guid.Empty : rd.GetGuid(1);
                    testName = rd.IsDBNull(2) ? "" : rd.GetString(2);
                }

                // 2) Escalas + ítems (ya tienes este método en el repo)
                var scalesWithItems = await GetScalesWithItemsAsync(testId, ct); // IReadOnlyList<ScaleWithItemsDto>

                // 3) Respuestas del intento (ya tienes este método en el repo)
                //    ownerUserId = null, isAdmin = true -> sin filtro
                var answers = await GetAttemptAnswersAsync(attemptId, null, true, ct); // IReadOnlyList<AttemptAnswerRow>

                // 4) (Opcional) Mín/Máx por pregunta a partir de opciones configuradas.
                //    Si no hay opciones, usamos fallback 1..4 (Likert típico).
                //    Para evitar usar OPENJSON del lado SQL, lo haremos todo en C#.
                var qMinMax = new Dictionary<Guid, (decimal min, decimal max)>();
                // intentamos leer opciones si existe tabla test_question_options
                bool hasOptions = false;
                try
                {
                    const string SQL_HAS = @"SELECT COUNT(1) FROM sys.objects WHERE object_id = OBJECT_ID(N'dbo.test_question_options') AND type = 'U';";
                    await using (var check = new SqlCommand(SQL_HAS, cn))
                    {
                        var n = Convert.ToInt32(await check.ExecuteScalarAsync(ct));
                        hasOptions = n > 0;
                    }
                }
                catch { hasOptions = false; }

                if (hasOptions && scalesWithItems.Count > 0)
                {
                    // recolectar todos los question_ids a consultar
                    var qids = scalesWithItems
                        .SelectMany(s => s.Items.Select(it => it.Id))
                        .Distinct()
                        .ToList();

                    if (qids.Count > 0)
                    {
                        // Haremos varios IN por lotes pequeños para no construir un comando gigantesco
                        const int BATCH = 80;
                        for (int i = 0; i < qids.Count; i += BATCH)
                        {
                            var batch = qids.Skip(i).Take(BATCH).ToList();
                            // construimos un IN (@q0,@q1,...) seguro
                            var pars = string.Join(",", batch.Select((_, idx) => $"@q{idx}"));
                            var sqlOpts = $@"
SELECT o.question_id, o.value
FROM dbo.test_question_options o
WHERE o.question_id IN ({pars}) AND (o.is_active = 1 OR o.is_active IS NULL);";

                            await using var optCmd = new SqlCommand(sqlOpts, cn);
                            for (int k = 0; k < batch.Count; k++)
                                optCmd.Parameters.Add(new SqlParameter($"@q{k}", System.Data.SqlDbType.UniqueIdentifier) { Value = batch[k] });

                            var tmp = new Dictionary<Guid, List<decimal>>();
                            await using var rd = await optCmd.ExecuteReaderAsync(ct);
                            while (await rd.ReadAsync(ct))
                            {
                                var qid = rd.GetGuid(0);
                                var raw = rd.IsDBNull(1) ? null : rd.GetValue(1)?.ToString();
                                if (decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dv))
                                {
                                    if (!tmp.TryGetValue(qid, out var list)) { list = new List<decimal>(); tmp[qid] = list; }
                                    list.Add(dv);
                                }
                            }

                            foreach (var kv in tmp)
                            {
                                if (kv.Value.Count > 0)
                                    qMinMax[kv.Key] = (kv.Value.Min(), kv.Value.Max());
                            }
                        }
                    }
                }

                // 5) Mapear respuestas -> valor numérico por pregunta (C#), sin OPENJSON
                decimal Coerce(string? s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return decimal.MinValue;
                    if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                        return d;
                    return decimal.MinValue;
                }

                decimal? ValueFromAnswer(AttemptAnswerRow a)
                {
                    // a.AnswerValue (string) primero
                    var v = Coerce(a.AnswerValue);
                    if (v != decimal.MinValue) return v;

                    // Si viene array en JSON (ej. ["1","3"]) lo parseamos en C#
                    if (!string.IsNullOrWhiteSpace(a.AnswerValuesJson))
                    {
                        try
                        {
                            var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(a.AnswerValuesJson);
                            if (arr != null && arr.Count > 0)
                            {
                                decimal sum = 0m; int count = 0;
                                foreach (var s in arr)
                                {
                                    var n = Coerce(s);
                                    if (n != decimal.MinValue) { sum += n; count++; }
                                }
                                if (count > 0) return sum;
                            }
                        }
                        catch { /* ignorar parse error */ }
                    }

                    return null;
                }

                // 6) Calcular escalas actuales
                var current = new List<ScaleRow>();
                foreach (var s in scalesWithItems)
                {
                    decimal raw = 0m, min = 0m, max = 0m;

                    foreach (var it in s.Items)
                    {
                        // min/max por pregunta
                        if (!qMinMax.TryGetValue(it.Id, out var mm)) mm = (1m, 4m); // fallback Likert 1..4
                        min += mm.min; max += mm.max;

                        // buscar respuesta
                        var a = answers.FirstOrDefault(x => x.QuestionId == it.Id);
                        var val = (a == null) ? (decimal?)null : ValueFromAnswer(a);

                        // si no hay respuesta numérica válida, usar min por defecto
                        raw += (val ?? mm.min);
                    }

                    current.Add(new ScaleRow(
                        Code: s.Code ?? "",
                        Name: s.Name ?? "",
                        Raw: raw,
                        Min: min,
                        Max: max
                    ));
                }

                // 7) (fase 1) Entrevista inicial + tests previos: lo dejamos vacío para no romper nada ahora.
                string? initialInterview = null;
                var previous = Array.Empty<TestSummary>();

                return new AttemptAiBundle(
                    AttemptId: attemptId,
                    PatientId: patientId,
                    TestName: testName,
                    CurrentScales: current,
                    InitialInterviewText: initialInterview,
                    PreviousTests: previous
                );
            }
        }        
    }
}
