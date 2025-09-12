using EPApi.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text;
using static EPApi.Models.TestCrudDto;

namespace EPApi.DataAccess
{
    public sealed class TestRepository : ITestRepository
    {
        private readonly string _cs;
        public TestRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                  ?? throw new InvalidOperationException("Missing Default connection string 'Default'");
        }

        public async Task<(IReadOnlyList<TestListItem> Items, int Total)> GetPagedAsync(
            int page, int pageSize, string? search, CancellationToken ct = default)
        {
            if (page < 1) page = 1;
            if (pageSize <= 0 || pageSize > 200) pageSize = 50;

            const string sql = @"
;WITH T AS (
  SELECT
    t.id, t.code, t.name, t.description, t.pdf_url, t.is_active,
    t.created_at, t.updated_at,
    ag.code AS age_group_code, ag.name AS age_group_name,
    (SELECT COUNT(*) FROM dbo.test_questions  q WHERE q.test_id = t.id) AS question_count,
    (SELECT COUNT(*) FROM dbo.test_scales     s WHERE s.test_id = t.id) AS scale_count
  FROM dbo.tests t
  INNER JOIN dbo.age_groups ag ON ag.id = t.age_group_id
  WHERE (@search IS NULL OR @search = ''
         OR t.name LIKE '%' + @search + '%'
         OR t.code LIKE '%' + @search + '%')
)
SELECT COUNT(1) AS total FROM T;

WITH T AS (
  SELECT
    t.id, t.code, t.name, t.description, t.pdf_url, t.is_active,
    t.created_at, t.updated_at,
    ag.code AS age_group_code, ag.name AS age_group_name,
    (SELECT COUNT(*) FROM dbo.test_questions  q WHERE q.test_id = t.id) AS question_count,
    (SELECT COUNT(*) FROM dbo.test_scales     s WHERE s.test_id = t.id) AS scale_count
  FROM dbo.tests t
  INNER JOIN dbo.age_groups ag ON ag.id = t.age_group_id
  WHERE (@search IS NULL OR @search = ''
         OR t.name LIKE '%' + @search + '%'
         OR t.code LIKE '%' + @search + '%')
)
SELECT *
FROM T
ORDER BY name
OFFSET (@off) ROWS FETCH NEXT (@ps) ROWS ONLY;
";

            var items = new List<TestListItem>();
            var total = 0;

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@search", SqlDbType.NVarChar, 255) { Value = (object?)search ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@off", SqlDbType.Int) { Value = (page - 1) * pageSize });
            cmd.Parameters.Add(new SqlParameter("@ps", SqlDbType.Int) { Value = pageSize });

            await using var rd = await cmd.ExecuteReaderAsync(ct);

            if (await rd.ReadAsync(ct))
                total = rd.GetInt32(0);

            if (await rd.NextResultAsync(ct))
            {
                while (await rd.ReadAsync(ct))
                {
                    items.Add(new TestListItem
                    {
                        Id = rd.GetGuid(0),
                        Code = rd.GetString(1),
                        Name = rd.GetString(2),
                        Description = rd.IsDBNull(3) ? null : rd.GetString(3),
                        PdfUrl = rd.IsDBNull(4) ? null : rd.GetString(4),
                        IsActive = rd.GetBoolean(5),
                        CreatedAt = rd.GetDateTime(6),
                        UpdatedAt = rd.GetDateTime(7),
                        AgeGroupCode = rd.GetString(8),
                        AgeGroupName = rd.GetString(9),
                        QuestionCount = rd.GetInt32(10),
                        ScaleCount = rd.GetInt32(11)
                    });
                }
            }

            return (items, total);
        }

        public async Task<TestDetail?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            // Añadimos t.age_group_id para exponer AgeGroupId (GUID)
            const string sql = @"
SELECT
  t.id, t.code, t.name, t.description, t.instructions, t.example, t.pdf_url,
  t.age_group_id,                            -- <- NUEVO en SELECT
  t.is_active, t.created_at, t.updated_at,
  ag.code AS age_group_code, ag.name AS age_group_name,
  (SELECT COUNT(*) FROM dbo.test_questions q WHERE q.test_id = t.id) AS question_count,
  (SELECT COUNT(*) FROM dbo.test_scales    s WHERE s.test_id = t.id) AS scale_count
FROM dbo.tests t
JOIN dbo.age_groups ag ON ag.id = t.age_group_id
WHERE t.id = @id;
";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;

            return new TestDetail
            {
                Id = rd.GetGuid(0),
                Code = rd.GetString(1),
                Name = rd.GetString(2),
                Description = rd.IsDBNull(3) ? null : rd.GetString(3),
                Instructions = rd.IsDBNull(4) ? null : rd.GetString(4),
                Example = rd.IsDBNull(5) ? null : rd.GetString(5),
                PdfUrl = rd.IsDBNull(6) ? null : rd.GetString(6),

                AgeGroupId = rd.GetGuid(7),               // <- NUEVO mapeo

                IsActive = rd.GetBoolean(8),
                CreatedAt = rd.GetDateTime(9),
                UpdatedAt = rd.GetDateTime(10),

                AgeGroupCode = rd.GetString(11),
                AgeGroupName = rd.GetString(12),

                QuestionCount = rd.GetInt32(13),
                ScaleCount = rd.GetInt32(14)
            };
        }

        public async Task<IReadOnlyList<TestQuestionRow>> GetQuestionsAsync(Guid testId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT q.id, q.code, q.text, q.question_type, q.order_no, q.is_optional
FROM dbo.test_questions q
WHERE q.test_id = @id
ORDER BY q.order_no, q.code;
";
            var list = new List<TestQuestionRow>();
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = testId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new TestQuestionRow
                {
                    Id = rd.GetGuid(0),
                    Code = rd.GetString(1),
                    Text = rd.GetString(2),
                    QuestionType = rd.GetString(3),
                    OrderNo = rd.GetInt32(4),
                    IsOptional = rd.GetBoolean(5)
                });
            }
            return list;
        }

        public async Task<IReadOnlyList<TestScaleRow>> GetScalesAsync(Guid testId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT s.id, s.code, s.name, s.description,
       COUNT(tsq.question_id) AS question_count
FROM dbo.test_scales s
LEFT JOIN dbo.test_scale_questions tsq ON tsq.scale_id = s.id
WHERE s.test_id = @id
GROUP BY s.id, s.code, s.name, s.description
ORDER BY s.name;
";
            var list = new List<TestScaleRow>();
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = testId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new TestScaleRow
                {
                    Id = rd.GetGuid(0),
                    Code = rd.GetString(1),
                    Name = rd.GetString(2),
                    Description = rd.IsDBNull(3) ? null : rd.GetString(3),
                    QuestionCount = rd.GetInt32(4)
                });
            }
            return list;
        }

        public async Task<Guid> CreateAsync(TestCreateDto dto, CancellationToken ct = default)
        {
            const string sql = @"
INSERT INTO dbo.tests (id, code, name, description, instructions, example, pdf_url, age_group_id, is_active, created_at, updated_at)
VALUES (@id, @code, @name, @desc, @instr, @ex, @pdf, @ag, @act, SYSDATETIME(), SYSDATETIME());
";
            var id = Guid.NewGuid();
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddRange(new[]
            {
                new SqlParameter("@id",   SqlDbType.UniqueIdentifier){ Value = id },
                new SqlParameter("@code", SqlDbType.NVarChar, 50){ Value = dto.Code },
                new SqlParameter("@name", SqlDbType.NVarChar, 255){ Value = dto.Name },
                new SqlParameter("@desc", SqlDbType.NVarChar, -1){ Value = (object?)dto.Description ?? DBNull.Value },
                new SqlParameter("@instr",SqlDbType.NVarChar, -1){ Value = (object?)dto.Instructions ?? DBNull.Value },
                new SqlParameter("@ex",   SqlDbType.NVarChar, -1){ Value = (object?)dto.Example ?? DBNull.Value },
                new SqlParameter("@pdf",  SqlDbType.NVarChar, 1024){ Value = (object?)dto.PdfUrl ?? DBNull.Value },
                new SqlParameter("@ag",   SqlDbType.UniqueIdentifier){ Value = dto.AgeGroupId },
                new SqlParameter("@act",  SqlDbType.Bit){ Value = dto.IsActive },
            });
            await cmd.ExecuteNonQueryAsync(ct);
            return id;
        }

        public async Task<bool> UpdateAsync(Guid id, TestUpdateDto dto, CancellationToken ct = default)
        {
            const string sql = @"
UPDATE dbo.tests
SET name=@name, description=@desc, instructions=@instr, example=@ex,
    pdf_url=@pdf, age_group_id=@ag, is_active=@act, updated_at=SYSDATETIME()
WHERE id=@id;
";
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddRange(new[]
            {
                new SqlParameter("@id",   SqlDbType.UniqueIdentifier){ Value = id },
                new SqlParameter("@name", SqlDbType.NVarChar, 255){ Value = dto.Name },
                new SqlParameter("@desc", SqlDbType.NVarChar, -1){ Value = (object?)dto.Description ?? DBNull.Value },
                new SqlParameter("@instr",SqlDbType.NVarChar, -1){ Value = (object?)dto.Instructions ?? DBNull.Value },
                new SqlParameter("@ex",   SqlDbType.NVarChar, -1){ Value = (object?)dto.Example ?? DBNull.Value },
                new SqlParameter("@pdf",  SqlDbType.NVarChar, 1024){ Value = (object?)dto.PdfUrl ?? DBNull.Value },
                new SqlParameter("@ag",   SqlDbType.UniqueIdentifier){ Value = dto.AgeGroupId },
                new SqlParameter("@act",  SqlDbType.Bit){ Value = dto.IsActive },
            });
            var n = await cmd.ExecuteNonQueryAsync(ct);
            return n > 0;
        }

        public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        {
            // Eliminación en cascada manual (ajústalo si añades respuestas/asignaciones)
            const string sql = @"
BEGIN TRAN;
  DELETE tsq
  FROM dbo.test_scale_questions tsq
  JOIN dbo.test_scales s ON s.id = tsq.scale_id
  WHERE s.test_id = @id;

  DELETE FROM dbo.test_scales    WHERE test_id = @id;
  DELETE FROM dbo.test_questions WHERE test_id = @id;
  DELETE FROM dbo.tests          WHERE id      = @id;
COMMIT TRAN;
";
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            await cmd.ExecuteNonQueryAsync(ct);

            const string check = "SELECT COUNT(1) FROM dbo.tests WHERE id=@id;";
            await using var cmd2 = new SqlCommand(check, cn);
            cmd2.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            var remaining = (int)await cmd2.ExecuteScalarAsync(ct);
            return remaining == 0;
        }

        public async Task<Guid> CreateQuestionAsync(Guid testId, TestQuestionCreateDto dto, CancellationToken ct = default)
        {
            const string sql = @"
INSERT INTO dbo.test_questions (id, test_id, code, text, question_type, order_no, is_optional, created_at, updated_at)
VALUES (@id, @test_id, @code, @text, @qt, @ord, @opt, SYSDATETIME(), SYSDATETIME());
";
            var id = Guid.NewGuid();
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddRange(new[]
            {
                new SqlParameter("@id",      SqlDbType.UniqueIdentifier){ Value = id },
                new SqlParameter("@test_id", SqlDbType.UniqueIdentifier){ Value = testId },
                new SqlParameter("@code",    SqlDbType.NVarChar, 50){ Value = dto.Code },
                new SqlParameter("@text",    SqlDbType.NVarChar, -1){ Value = dto.Text },
                new SqlParameter("@qt",      SqlDbType.NVarChar, 50){ Value = dto.QuestionType },
                new SqlParameter("@ord",     SqlDbType.Int){ Value = dto.OrderNo },
                new SqlParameter("@opt",     SqlDbType.Bit){ Value = dto.IsOptional },
            });
            await cmd.ExecuteNonQueryAsync(ct);
            return id;
        }

        public async Task<bool> UpdateQuestionAsync(Guid testId, Guid questionId, TestQuestionUpdateDto dto, CancellationToken ct = default)
        {
            const string sql = @"
UPDATE dbo.test_questions
SET text=@text, question_type=@qt, order_no=@ord, is_optional=@opt, updated_at=SYSDATETIME()
WHERE id=@id AND test_id=@test_id;
";
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.AddRange(new[]
            {
                new SqlParameter("@id",      SqlDbType.UniqueIdentifier){ Value = questionId },
                new SqlParameter("@test_id", SqlDbType.UniqueIdentifier){ Value = testId },
                new SqlParameter("@text",    SqlDbType.NVarChar, -1){ Value = dto.Text },
                new SqlParameter("@qt",      SqlDbType.NVarChar, 50){ Value = dto.QuestionType },
                new SqlParameter("@ord",     SqlDbType.Int){ Value = dto.OrderNo },
                new SqlParameter("@opt",     SqlDbType.Bit){ Value = dto.IsOptional },
            });
            var n = await cmd.ExecuteNonQueryAsync(ct);
            return n > 0;
        }

        public async Task<bool> DeleteQuestionAsync(Guid testId, Guid questionId, CancellationToken ct = default)
        {
            const string sql = @"
DELETE FROM dbo.test_scale_questions WHERE question_id=@id;
DELETE FROM dbo.test_questions       WHERE id=@id AND test_id=@test_id;
";
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = questionId });
            cmd.Parameters.Add(new SqlParameter("@test_id", SqlDbType.UniqueIdentifier) { Value = testId });
            await cmd.ExecuteNonQueryAsync(ct);

            const string check = "SELECT COUNT(1) FROM dbo.test_questions WHERE id=@id AND test_id=@test_id;";
            await using var cmd2 = new SqlCommand(check, cn);
            cmd2.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = questionId });
            cmd2.Parameters.Add(new SqlParameter("@test_id", SqlDbType.UniqueIdentifier) { Value = testId });
            var remaining = (int)await cmd2.ExecuteScalarAsync(ct);
            return remaining == 0;
        }

        public async Task<IReadOnlyList<TestQuestionOptionRow>> GetQuestionOptionsByTestAsync(Guid testId, CancellationToken ct = default)
        {
            // Importante: test_question_options no tiene 'code'
            const string sql = @"
SELECT
    o.id,
    o.question_id,
    o.value,
    o.label,
    o.order_no,
    o.is_active
FROM dbo.test_question_options AS o
JOIN dbo.test_questions      AS q ON q.id = o.question_id
WHERE q.test_id = @test_id
ORDER BY q.order_no, o.order_no;";

            var list = new List<TestQuestionOptionRow>(256);

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@test_id", SqlDbType.UniqueIdentifier) { Value = testId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new TestQuestionOptionRow
                {
                    Id = rd.GetGuid(0),
                    QuestionId = rd.GetGuid(1),
                    Value = rd.GetInt32(2),
                    Label = rd.GetString(3),
                    OrderNo = rd.GetInt32(4),
                    IsActive = rd.GetBoolean(5),
                });
            }

            return list;
        }

        public async Task<TestDisciplinesReadDto> GetDisciplinesAsync(Guid testId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT d.id, d.code, d.name
FROM dbo.test_disciplines td
JOIN dbo.disciplines d ON d.id = td.discipline_id
WHERE td.test_id = @tid
ORDER BY d.name;";

            var dto = new TestDisciplinesReadDto();

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = testId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                dto.Disciplines.Add(new TestDisciplineItem
                {
                    Id = rd.GetInt32(0),
                    Code = rd.GetString(1),
                    Name = rd.GetString(2)
                });
            }
            return dto;
        }

        public async Task ReplaceDisciplinesAsync(Guid testId, int[] disciplineIds, CancellationToken ct = default)
        {
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(ct);

            try
            {
                // 1) Borra vínculos actuales
                const string del = "DELETE FROM dbo.test_disciplines WHERE test_id = @tid;";
                await using (var cmdDel = new SqlCommand(del, cn, (SqlTransaction)tx))
                {
                    cmdDel.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = testId });
                    await cmdDel.ExecuteNonQueryAsync(ct);
                }

                // 2) Inserta los nuevos (si hay)
                var ids = (disciplineIds ?? Array.Empty<int>()).Distinct().ToArray();
                if (ids.Length > 0)
                {
                    // construimos un INSERT multi-values parametrizado: (@tid,@d0),(@tid,@d1),...
                    var sb = new System.Text.StringBuilder();
                    sb.Append("INSERT INTO dbo.test_disciplines (test_id, discipline_id) VALUES ");

                    for (int i = 0; i < ids.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append($"(@tid,@d{i})");
                    }
                    sb.Append(';');

                    await using var cmdIns = new SqlCommand(sb.ToString(), cn, (SqlTransaction)tx);
                    cmdIns.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = testId });
                    for (int i = 0; i < ids.Length; i++)
                    {
                        cmdIns.Parameters.Add(new SqlParameter($"@d{i}", SqlDbType.Int) { Value = ids[i] });
                    }

                    await cmdIns.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        public async Task<IReadOnlyList<TestTaxonomyRow>> GetTaxonomyAsync(Guid testId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT
  d.id   AS discipline_id,  d.code AS discipline_code,  d.name AS discipline_name,
  c.id   AS category_id,    c.code AS category_code,    c.name AS category_name,
  s.id   AS subcategory_id, s.code AS subcategory_code, s.name AS subcategory_name
FROM dbo.tests_taxonomy tt
JOIN dbo.disciplines  d ON d.id = tt.discipline_id
LEFT JOIN dbo.categories    c ON c.id = tt.category_id
LEFT JOIN dbo.subcategories s ON s.id = tt.subcategory_id
WHERE tt.test_id = @id
ORDER BY d.name, c.name, s.name;";

            var list = new List<TestTaxonomyRow>(32);
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = testId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new TestTaxonomyRow
                {
                    DisciplineId = rd.GetInt32(0),
                    DisciplineCode = rd.GetString(1),
                    DisciplineName = rd.GetString(2),
                    CategoryId = rd.IsDBNull(3) ? (int?)null : rd.GetInt32(3),
                    CategoryCode = rd.IsDBNull(4) ? null : rd.GetString(4),
                    CategoryName = rd.IsDBNull(5) ? null : rd.GetString(5),
                    SubcategoryId = rd.IsDBNull(6) ? (int?)null : rd.GetInt32(6),
                    SubcategoryCode = rd.IsDBNull(7) ? null : rd.GetString(7),
                    SubcategoryName = rd.IsDBNull(8) ? null : rd.GetString(8),
                });
            }
            return list;
        }

        public async Task ReplaceTaxonomyAsync(Guid testId, IEnumerable<TestTaxonomyWriteItem> items, CancellationToken ct = default)
        {
            // Normalizamos (quitamos duplicados exactos)
            var uniq = items
              .Select(x => new { x.DisciplineId, x.CategoryId, x.SubcategoryId })
              .Distinct()
              .ToArray();

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            // Limpiar vínculos previos
            const string del = "DELETE FROM dbo.tests_taxonomy WHERE test_id=@id;";
            await using (var cmdDel = new SqlCommand(del, cn))
            {
                cmdDel.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = testId });
                await cmdDel.ExecuteNonQueryAsync(ct);
            }

            if (uniq.Length == 0) return;

            // Insert por lotes con parámetros nombrados
            var sb = new StringBuilder();
            sb.Append("INSERT INTO dbo.tests_taxonomy (test_id, discipline_id, category_id, subcategory_id) VALUES ");
            for (int i = 0; i < uniq.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.AppendFormat("(@id, @d{0}, @c{0}, @s{0})", i);
            }
            sb.Append(';');

            await using (var cmdIns = new SqlCommand(sb.ToString(), cn))
            {
                cmdIns.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = testId });
                for (int i = 0; i < uniq.Length; i++)
                {
                    cmdIns.Parameters.Add(new SqlParameter("@d" + i, SqlDbType.Int) { Value = uniq[i].DisciplineId });
                    cmdIns.Parameters.Add(new SqlParameter("@c" + i, SqlDbType.Int) { Value = (object?)uniq[i].CategoryId ?? DBNull.Value });
                    cmdIns.Parameters.Add(new SqlParameter("@s" + i, SqlDbType.Int) { Value = (object?)uniq[i].SubcategoryId ?? DBNull.Value });
                }
                await cmdIns.ExecuteNonQueryAsync(ct);
            }

            // El trigger validará coherencia jerárquica.
        }

        // ========= MÉTODO ORIGINAL (compat) =========
        public async Task<(IReadOnlyList<TestListItem> Items, int Total)> GetForUserAsync(
            int userId, int page, int pageSize, string? search, CancellationToken ct = default)
        {
            if (page < 1) page = 1;
            if (pageSize <= 0 || pageSize > 200) pageSize = 24;

            const string sql = @"
DECLARE @hasPractices bit =
    CASE WHEN EXISTS (SELECT 1 FROM dbo.user_disciplines pd WHERE pd.user_id = @uid) THEN 1 ELSE 0 END;

;WITH T AS (
    SELECT
        t.id, t.code, t.name, t.description, t.pdf_url, t.is_active,
        t.created_at, t.updated_at,
        ag.code AS age_group_code, ag.name AS age_group_name,
        (SELECT COUNT(*) FROM dbo.test_questions  q WHERE q.test_id = t.id) AS question_count,
        (SELECT COUNT(*) FROM dbo.test_scales     s WHERE s.test_id = t.id) AS scale_count
    FROM dbo.tests t
    INNER JOIN dbo.age_groups ag ON ag.id = t.age_group_id
    WHERE t.is_active = 1
      AND (@search IS NULL OR @search = '' OR t.name LIKE '%' + @search + '%' OR t.code LIKE '%' + @search + '%')
      AND (
            @hasPractices = 1
            AND EXISTS (
                SELECT 1
                FROM dbo.tests_taxonomy tx
                INNER JOIN dbo.user_disciplines pd
                    ON pd.discipline_id = tx.discipline_id AND pd.user_id = @uid
                WHERE tx.test_id = t.id
            )
      )
)
SELECT COUNT(1) AS total FROM T;

WITH T AS (
    SELECT
        t.id, t.code, t.name, t.description, t.pdf_url, t.is_active,
        t.created_at, t.updated_at,
        ag.code AS age_group_code, ag.name AS age_group_name,
        (SELECT COUNT(*) FROM dbo.test_questions  q WHERE q.test_id = t.id) AS question_count,
        (SELECT COUNT(*) FROM dbo.test_scales     s WHERE s.test_id = t.id) AS scale_count
    FROM dbo.tests t
    INNER JOIN dbo.age_groups ag ON ag.id = t.age_group_id
    WHERE t.is_active = 1
      AND (@search IS NULL OR @search = '' OR t.name LIKE '%' + @search + '%' OR t.code LIKE '%' + @search + '%')
      AND (
            @hasPractices = 1
            AND EXISTS (
                SELECT 1
                FROM dbo.tests_taxonomy tx
                INNER JOIN dbo.user_disciplines pd
                    ON pd.discipline_id = tx.discipline_id AND pd.user_id = @uid
                WHERE tx.test_id = t.id
            )
      )
)
SELECT *
FROM T
ORDER BY name
OFFSET (@off) ROWS FETCH NEXT (@ps) ROWS ONLY;
";

            var items = new List<TestListItem>();
            var total = 0;

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
            cmd.Parameters.Add(new SqlParameter("@search", SqlDbType.NVarChar, 255) { Value = (object?)search ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@off", SqlDbType.Int) { Value = (page - 1) * pageSize });
            cmd.Parameters.Add(new SqlParameter("@ps", SqlDbType.Int) { Value = pageSize });

            await using var rd = await cmd.ExecuteReaderAsync(ct);

            if (await rd.ReadAsync(ct))
                total = rd.GetInt32(0);

            if (await rd.NextResultAsync(ct))
            {
                while (await rd.ReadAsync(ct))
                {
                    items.Add(new TestListItem
                    {
                        Id = rd.GetGuid(0),
                        Code = rd.GetString(1),
                        Name = rd.GetString(2),
                        Description = rd.IsDBNull(3) ? null : rd.GetString(3),
                        PdfUrl = rd.IsDBNull(4) ? null : rd.GetString(4),
                        IsActive = rd.GetBoolean(5),
                        CreatedAt = rd.GetDateTime(6),
                        UpdatedAt = rd.GetDateTime(7),
                        AgeGroupCode = rd.GetString(8),
                        AgeGroupName = rd.GetString(9),
                        QuestionCount = rd.GetInt32(10),
                        ScaleCount = rd.GetInt32(11)
                    });
                }
            }

            return (items, total);
        }

        // ========= NUEVO OVERLOAD: con filtros + taxonomy en payload =========
        public async Task<(IReadOnlyList<TestListItemDto> Items, int Total)> GetForUserAsync(
            int userId, int page, int pageSize, string? search, TestsForMeFilters? filters, CancellationToken ct = default)
        {
            if (page < 1) page = 1;
            if (pageSize <= 0 || pageSize > 200) pageSize = 24;
            filters ??= new TestsForMeFilters();

            const string sql = @"
DECLARE @hasPractices bit =
    CASE WHEN EXISTS (SELECT 1 FROM dbo.user_disciplines pd WHERE pd.user_id = @uid) THEN 1 ELSE 0 END;

;WITH T AS (
    SELECT
        t.id, t.code, t.name, t.description, t.pdf_url, t.is_active,
        t.created_at, t.updated_at,
        ag.code AS age_group_code, ag.name AS age_group_name,
        (SELECT COUNT(*) FROM dbo.test_questions  q WHERE q.test_id = t.id) AS question_count,
        (SELECT COUNT(*) FROM dbo.test_scales     s WHERE s.test_id = t.id) AS scale_count
    FROM dbo.tests t
    INNER JOIN dbo.age_groups ag ON ag.id = t.age_group_id
    WHERE t.is_active = 1
      AND (@search IS NULL OR @search = '' OR t.name LIKE '%' + @search + '%' OR t.code LIKE '%' + @search + '%')
      AND (
            @hasPractices = 1
            AND EXISTS (
                SELECT 1
                FROM dbo.tests_taxonomy tx
                INNER JOIN dbo.user_disciplines pd
                    ON pd.discipline_id = tx.discipline_id AND pd.user_id = @uid
                WHERE tx.test_id = t.id
            )
      )
      -- Filtro por disciplina (id/code) si viene
      AND (
            (@discId IS NULL AND @discCode IS NULL)
            OR EXISTS (
                SELECT 1
                FROM dbo.tests_taxonomy tx
                JOIN dbo.disciplines d ON d.id = tx.discipline_id
                WHERE tx.test_id = t.id
                  AND (@discId   IS NULL OR tx.discipline_id = @discId)
                  AND (@discCode IS NULL OR d.code = @discCode)
            )
      )
      -- Filtro por categoría (id/code) si viene
      AND (
            (@catId IS NULL AND @catCode IS NULL)
            OR EXISTS (
                SELECT 1
                FROM dbo.tests_taxonomy tx
                JOIN dbo.categories c ON c.id = tx.category_id
                WHERE tx.test_id = t.id
                  AND (@catId   IS NULL OR tx.category_id = @catId)
                  AND (@catCode IS NULL OR c.code = @catCode)
            )
      )
      -- Filtro por subcategoría (id/code) si viene
      AND (
            (@subId IS NULL AND @subCode IS NULL)
            OR EXISTS (
                SELECT 1
                FROM dbo.tests_taxonomy tx
                JOIN dbo.subcategories s ON s.id = tx.subcategory_id
                WHERE tx.test_id = t.id
                  AND (@subId   IS NULL OR tx.subcategory_id = @subId)
                  AND (@subCode IS NULL OR s.code = @subCode)
            )
      )
)
SELECT COUNT(1) AS total FROM T;

WITH T AS (
    SELECT
        t.id, t.code, t.name, t.description, t.pdf_url, t.is_active,
        t.created_at, t.updated_at,
        ag.code AS age_group_code, ag.name AS age_group_name,
        (SELECT COUNT(*) FROM dbo.test_questions  q WHERE q.test_id = t.id) AS question_count,
        (SELECT COUNT(*) FROM dbo.test_scales     s WHERE s.test_id = t.id) AS scale_count
    FROM dbo.tests t
    INNER JOIN dbo.age_groups ag ON ag.id = t.age_group_id
    WHERE t.is_active = 1
      AND (@search IS NULL OR @search = '' OR t.name LIKE '%' + @search + '%' OR t.code LIKE '%' + @search + '%')
      AND (
            @hasPractices = 1
            AND EXISTS (
                SELECT 1
                FROM dbo.tests_taxonomy tx
                INNER JOIN dbo.user_disciplines pd
                    ON pd.discipline_id = tx.discipline_id AND pd.user_id = @uid
                WHERE tx.test_id = t.id
            )
      )
      AND (
            (@discId IS NULL AND @discCode IS NULL)
            OR EXISTS (
                SELECT 1
                FROM dbo.tests_taxonomy tx
                JOIN dbo.disciplines d ON d.id = tx.discipline_id
                WHERE tx.test_id = t.id
                  AND (@discId   IS NULL OR tx.discipline_id = @discId)
                  AND (@discCode IS NULL OR d.code = @discCode)
            )
      )
      AND (
            (@catId IS NULL AND @catCode IS NULL)
            OR EXISTS (
                SELECT 1
                FROM dbo.tests_taxonomy tx
                JOIN dbo.categories c ON c.id = tx.category_id
                WHERE tx.test_id = t.id
                  AND (@catId   IS NULL OR tx.category_id = @catId)
                  AND (@catCode IS NULL OR c.code = @catCode)
            )
      )
      AND (
            (@subId IS NULL AND @subCode IS NULL)
            OR EXISTS (
                SELECT 1
                FROM dbo.tests_taxonomy tx
                JOIN dbo.subcategories s ON s.id = tx.subcategory_id
                WHERE tx.test_id = t.id
                  AND (@subId   IS NULL OR tx.subcategory_id = @subId)
                  AND (@subCode IS NULL OR s.code = @subCode)
            )
      )
)
SELECT *
FROM T
ORDER BY name
OFFSET (@off) ROWS FETCH NEXT (@ps) ROWS ONLY;
";

            var items = new List<TestListItemDto>();
            var total = 0;

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            // 1) Ejecuta la consulta de conteo + página
            await using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
                cmd.Parameters.Add(new SqlParameter("@search", SqlDbType.NVarChar, 255) { Value = (object?)search ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@discId", SqlDbType.Int) { Value = (object?)filters.DisciplineId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@discCode", SqlDbType.NVarChar, 50) { Value = (object?)filters.DisciplineCode ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@catId", SqlDbType.Int) { Value = (object?)filters.CategoryId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@catCode", SqlDbType.NVarChar, 50) { Value = (object?)filters.CategoryCode ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@subId", SqlDbType.Int) { Value = (object?)filters.SubcategoryId ?? DBNull.Value });
                cmd.Parameters.Add(new SqlParameter("@subCode", SqlDbType.NVarChar, 50) { Value = (object?)filters.SubcategoryCode ?? DBNull.Value });

                cmd.Parameters.Add(new SqlParameter("@off", SqlDbType.Int) { Value = (page - 1) * pageSize });
                cmd.Parameters.Add(new SqlParameter("@ps", SqlDbType.Int) { Value = pageSize });

                await using var rd = await cmd.ExecuteReaderAsync(ct);

                if (await rd.ReadAsync(ct))
                    total = rd.GetInt32(0);

                if (await rd.NextResultAsync(ct))
                {
                    while (await rd.ReadAsync(ct))
                    {
                        items.Add(new TestListItemDto
                        {
                            Id = rd.GetGuid(0),
                            Code = rd.GetString(1),
                            Name = rd.GetString(2),
                            Description = rd.IsDBNull(3) ? null : rd.GetString(3),
                            PdfUrl = rd.IsDBNull(4) ? null : rd.GetString(4),
                            IsActive = rd.GetBoolean(5),
                            CreatedAt = rd.GetDateTime(6),
                            UpdatedAt = rd.GetDateTime(7),
                            AgeGroupCode = rd.GetString(8),
                            AgeGroupName = rd.GetString(9),
                            QuestionCount = rd.GetInt32(10),
                            ScaleCount = rd.GetInt32(11)
                        });
                    }
                }
            }

            if (items.Count == 0) return (items, total);

            // 2) Cargar la TAXONOMÍA de esos tests (sin STRING_AGG, 2do query + mapeo en C#)
            var sb = new StringBuilder();
            sb.Append(@"
SELECT
  tt.test_id,
  d.id   AS discipline_id,  d.code AS discipline_code,  d.name AS discipline_name,
  c.id   AS category_id,    c.code AS category_code,    c.name AS category_name,
  s.id   AS subcategory_id, s.code AS subcategory_code, s.name AS subcategory_name
FROM dbo.tests_taxonomy tt
JOIN dbo.disciplines  d ON d.id = tt.discipline_id
LEFT JOIN dbo.categories    c ON c.id = tt.category_id
LEFT JOIN dbo.subcategories s ON s.id = tt.subcategory_id
WHERE tt.test_id IN (");

            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"@tid{i}");
            }
            sb.Append(") ORDER BY d.name, c.name, s.name;");

            var taxonomyByTest = new Dictionary<Guid, List<TaxonomyItemDto>>();

            await using (var cmd2 = new SqlCommand(sb.ToString(), cn))
            {
                for (int i = 0; i < items.Count; i++)
                    cmd2.Parameters.Add(new SqlParameter($"@tid{i}", SqlDbType.UniqueIdentifier) { Value = items[i].Id });

                await using var rd2 = await cmd2.ExecuteReaderAsync(ct);
                while (await rd2.ReadAsync(ct))
                {
                    var tid = rd2.GetGuid(0);
                    if (!taxonomyByTest.TryGetValue(tid, out var list))
                    {
                        list = new List<TaxonomyItemDto>(4);
                        taxonomyByTest[tid] = list;
                    }

                    list.Add(new TaxonomyItemDto
                    {
                        DisciplineId = rd2.GetInt32(1),
                        DisciplineCode = rd2.GetString(2),
                        DisciplineName = rd2.GetString(3),

                        CategoryId = rd2.IsDBNull(4) ? (int?)null : rd2.GetInt32(4),
                        CategoryCode = rd2.IsDBNull(5) ? null : rd2.GetString(5),
                        CategoryName = rd2.IsDBNull(6) ? null : rd2.GetString(6),

                        SubcategoryId = rd2.IsDBNull(7) ? (int?)null : rd2.GetInt32(7),
                        SubcategoryCode = rd2.IsDBNull(8) ? null : rd2.GetString(8),
                        SubcategoryName = rd2.IsDBNull(9) ? null : rd2.GetString(9),
                    });
                }
            }

            foreach (var it in items)
            {
                if (taxonomyByTest.TryGetValue(it.Id, out var list))
                {
                    // Distinct por (disc, cat, sub)
                    it.Taxonomy = list
                        .GroupBy(x => new { x.DisciplineId, x.CategoryId, x.SubcategoryId })
                        .Select(g => g.First())
                        .ToList();
                }
            }

            return (items, total);
        }

        public async Task<IReadOnlyList<TestScaleQuestionRow>> GetScaleQuestionMapAsync(Guid testId, CancellationToken ct = default)
        {
            var list = new List<TestScaleQuestionRow>();
            const string sql = @"
SELECT
    s.id   AS ScaleId,
    q.id   AS QuestionId,
    tsq.weight AS Weight,
    tsq.reverse AS Reverse
FROM dbo.test_scale_questions tsq
JOIN dbo.test_scales     s ON s.id = tsq.scale_id
JOIN dbo.test_questions  q ON q.id = tsq.question_id
WHERE s.test_id = @testId
ORDER BY s.id, q.id;";

            using var con = new SqlConnection(_cs); // ajusta a tu código
            await con.OpenAsync(ct);
            using var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@testId", testId);
            using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new TestScaleQuestionRow
                {
                    ScaleId = rd.GetGuid(0),
                    QuestionId = rd.GetGuid(1),
                    Weight = Convert.ToDouble(rd.GetValue(2)),
                    Reverse = rd.GetBoolean(3),
                });
            }
            return list;
        }

        public Task SaveRunAsync(TestRunSave dto, CancellationToken ct = default)
        {
            // Placeholder para compilar. En el siguiente paso hacemos:
            // - Insert en dbo.test_runs
            // - Insert en dbo.test_run_answers
            // - Cálculo de puntajes y dbo.test_run_scale_scores
            // - Opcional: persistir "raw" JSON para trazabilidad
            // Devuelvo un Guid temporal aquí.
            return Task.FromResult(Guid.NewGuid());
        }

    }
}
