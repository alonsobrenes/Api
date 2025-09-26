using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using EPApi.Models.Search;
using Microsoft.Data.SqlClient;

namespace EPApi.Services.Search
{
    public sealed class SearchService : ISearchService
    {
        private readonly string _cs;

        public SearchService(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing Default connection string");
        }

        // === Helpers para tokenización de q ===
        private static (string textQ, string[] labelCodes, string[] hashTags) ParseTokens(string raw)
        {
            raw = (raw ?? string.Empty).Trim();
            if (raw.Length == 0) return ("", Array.Empty<string>(), Array.Empty<string>());

            var labels = new List<string>();
            var tags = new List<string>();

            // label:codigo  -> código: letras/números/_/-
            var rxLabel = new Regex(@"\blabel:([A-Za-z0-9_\-]+)\b", RegexOptions.IgnoreCase);
            // #hashtag -> palabra sin espacios ni signos de puntuación finales típicos
            var rxTag = new Regex(@"#([^\s#;,]+)", RegexOptions.IgnoreCase);

            string working = raw;

            // labels
            working = rxLabel.Replace(working, m =>
            {
                var code = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(code)) labels.Add(code.ToLowerInvariant());
                return ""; // quitar del texto
            });

            // hashtags
            working = rxTag.Replace(working, m =>
            {
                var tag = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(tag)) tags.Add(tag.ToLowerInvariant());
                return ""; // quitar del texto
            });

            var text = working.Trim();
            return (text, labels.Distinct().ToArray(), tags.Distinct().ToArray());
        }

        public async Task<SearchResponseDto> SearchAsync(Guid orgId, SearchRequestDto req, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            // Normaliza entrada
            var rawQ = (req.Q ?? string.Empty).Trim();
            var (qText, labelCodesFromQ, hashtagsFromQ) = ParseTokens(rawQ);

            var types = (req.Types ?? Array.Empty<string>())
                        .Select(t => (t ?? "").Trim().ToLowerInvariant())
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .ToHashSet();

            var wantSessions = types.Count == 0 || types.Contains("session");
            var wantInterviews = types.Count == 0 || types.Contains("interview");
            var wantPatients = types.Count == 0 || types.Contains("patient");
            var wantTests = types.Count == 0 || types.Contains("test");
            var wantFiles = types.Count == 0 || types.Contains("attachment");

            // Paginación
            var page = Math.Max(1, req.Page);
            var take = Math.Clamp(req.PageSize, 1, 200);
            var skip = (page - 1) * take;

            var sbUnion = new StringBuilder();
            var prms = new List<SqlParameter>
            {
                new("@org",  SqlDbType.UniqueIdentifier){ Value = orgId },
                new("@q",    SqlDbType.NVarChar, 4000)   { Value = qText.Length == 0 ? DBNull.Value : qText },
                new("@from", SqlDbType.DateTime2)        { Value = (object?)req.DateFromUtc ?? DBNull.Value },
                new("@to",   SqlDbType.DateTime2)        { Value = (object?)req.DateToUtc   ?? DBNull.Value },
            };

            // Helpers: IN parametrizado
            string BuildInList<T>(IEnumerable<T> xs, string prefix, List<SqlParameter> bag, SqlDbType type, int? size = null)
            {
                var names = new List<string>(); int i = 0;
                foreach (var v in xs)
                {
                    var p = size is int s
                        ? new SqlParameter($"@{prefix}{i++}", type, s) { Value = (object?)v ?? DBNull.Value }
                        : new SqlParameter($"@{prefix}{i++}", type) { Value = (object?)v ?? DBNull.Value };
                    bag.Add(p);
                    names.Add(p.ParameterName);
                }
                return names.Count == 0 ? "" : string.Join(",", names);
            }

            // Usamos SOLO los tokens parseados del texto para filtros de labels/hashtags.
            // (No dependemos de req.Labels/req.Hashtags del DTO para evitar incompatibilidades de tipo.)
            var labelCodes = labelCodesFromQ ?? Array.Empty<string>();
            var hashtags = hashtagsFromQ ?? Array.Empty<string>();

            var labelCodesIn = BuildInList(labelCodes, "lcode", prms, SqlDbType.NVarChar, 64);
            var hashtagsIn = BuildInList(hashtags, "tag", prms, SqlDbType.NVarChar, 64);

            /* =========================
                 SUBQUERY: SESSIONS
               ========================= */
            if (wantSessions)
            {
                var where = new List<string> { "s.org_id = @org", "s.deleted_at_utc IS NULL" };

                if (qText.Length > 0)
                {
                    where.Add(@"
                        (
                        s.title            LIKE ('%' + @q + '%') OR
                        s.content_text     LIKE ('%' + @q + '%') OR
                        s.ai_tidy_text     LIKE ('%' + @q + '%') OR
                        s.ai_opinion_text  LIKE ('%' + @q + '%')
                        )");
                }
                if (req.DateFromUtc.HasValue) where.Add("(s.updated_at_utc >= @from OR s.created_at_utc >= @from)");
                if (req.DateToUtc.HasValue) where.Add("(s.updated_at_utc <  @to   OR s.created_at_utc <  @to)");

                var needLbl = labelCodes.Length > 0;
                var needTag = hashtags.Length > 0;

                var joinLbl = needLbl
                    ? @"INNER JOIN dbo.label_assignments la ON la.org_id = s.org_id AND la.target_type = 'session' AND la.target_id_guid = s.id
                        INNER JOIN dbo.labels l ON l.id = la.label_id AND l.org_id = s.org_id"
                    : "/* no labels */";
                var whereLbl = needLbl ? $"AND l.code IN ({labelCodesIn})" : "";

                var joinTag = needTag
                    ? @"INNER JOIN dbo.hashtag_links hl ON hl.org_id = s.org_id AND hl.target_type = 'session' AND hl.target_id_guid = s.id
                        INNER JOIN dbo.hashtags h ON h.id = hl.hashtag_id AND h.org_id = s.org_id"
                    : "/* no hashtags */";
                var whereTag = needTag ? $"AND h.tag IN ({hashtagsIn})" : "";

                var sqlSessions = $@"
SELECT
  CAST('session' AS nvarchar(20)) AS [type],
  CONVERT(nvarchar(36), s.id)     AS [id],
  s.title                         AS [title],
  LEFT(ISNULL(s.content_text, s.ai_tidy_text), 300) AS [snippet],
  s.updated_at_utc                AS [updatedAtUtc],
  s.patient_id AS [patient_id]
FROM dbo.patient_sessions s
{joinLbl}
{joinTag}
WHERE {string.Join(" AND ", where)}
{whereLbl}
{whereTag}
";
                sbUnion.AppendLine(sqlSessions);
                sbUnion.AppendLine("UNION ALL");
            }

            /* =========================
                 SUBQUERY: INTERVIEWS
               ========================= */
            if (wantInterviews)
            {
                // Filtramos org por org_members (patients no tiene org_id)
                var where = new List<string> { "m.org_id = @org" };

                if (qText.Length > 0)
                {
                    where.Add(@"
                        (
                        i.clinician_diagnosis LIKE ('%' + @q + '%') OR
                        d.content              LIKE ('%' + @q + '%') OR
                        t.[text]               LIKE ('%' + @q + '%')
                        )");
                }

                var needLbl = labelCodes.Length > 0;
                var needTag = hashtags.Length > 0;

                var joinLbl = needLbl
                    ? @"INNER JOIN dbo.label_assignments la_i ON la_i.target_type='interview' AND la_i.target_id_guid = i.id
                        INNER JOIN dbo.labels li ON li.id = la_i.label_id"
                    : "/* no labels */";
                var whereLbl = needLbl ? $"AND li.code IN ({labelCodesIn})" : "";

                var joinTag = needTag
                    ? @"INNER JOIN dbo.hashtag_links hil ON hil.target_type='interview' AND hil.target_id_guid = i.id
                        INNER JOIN dbo.hashtags hh ON hh.id = hil.hashtag_id"
                    : "/* no hashtags */";
                var whereTag = needTag ? $"AND hh.tag IN ({hashtagsIn})" : "";

                var sqlInterviews = $@"
SELECT
  CAST('interview' AS nvarchar(20))  AS [type],
  CONVERT(nvarchar(36), i.id)        AS [id],
  CAST(N'Entrevista' AS nvarchar(128)) AS [title],
  LEFT(i.any_text, 300)              AS [snippet],
  i.updated_at_utc                   AS [updatedAtUtc],
  i.patient_id AS [patient_id]
FROM (
  SELECT
    i.id,
    COALESCE(i.clinician_diagnosis, d.content, t.[text]) AS any_text,
    (
      SELECT MAX(x) FROM (VALUES
        (i.clinician_diagnosis_updated_at_utc),
        (t.updated_at_utc),
        (d.created_at_utc),
        (i.ended_at_utc),
        (i.started_at_utc)
      ) AS v(x)
    ) AS updated_at_utc,
    p.id AS [patient_id]
  FROM dbo.interviews i
  INNER JOIN dbo.patients p ON p.id = i.patient_id
  INNER JOIN dbo.org_members m ON m.user_id = p.created_by_user_id
  LEFT JOIN dbo.interview_ai_drafts     d ON d.interview_id = i.id
  LEFT JOIN dbo.interview_transcripts   t ON t.interview_id = i.id
  WHERE {string.Join(" AND ", where)}
) i
{joinLbl}
{joinTag}
WHERE 1=1
{(req.DateFromUtc.HasValue ? "AND i.updated_at_utc >= @from" : "")}
{(req.DateToUtc.HasValue ? "AND i.updated_at_utc <  @to" : "")}
{whereLbl}
{whereTag}
";
                sbUnion.AppendLine(sqlInterviews);
                sbUnion.AppendLine("UNION ALL");
            }

            /* =========================
                 SUBQUERY: PATIENTS
               =========================
               - Filtrado de organización vía org_members
            */
            if (wantPatients)
            {
                var where = new List<string> { "1=1" };

                if (qText.Length > 0)
                {
                    where.Add(@"
                          (
                          p.first_name LIKE ('%' + @q + '%') OR
                          p.last_name1 LIKE ('%' + @q + '%') OR
                          p.last_name2 LIKE ('%' + @q + '%') OR
                          p.identification_number LIKE ('%' + @q + '%') OR
                          p.identification_type   LIKE ('%' + @q + '%') OR
                          p.[description]         LIKE ('%' + @q + '%') OR
                          LTRIM(RTRIM(COALESCE(p.first_name,'') + N' ' + COALESCE(p.last_name1,'') + N' ' + COALESCE(p.last_name2,''))) LIKE ('%' + @q + '%')
                          )");
                }
                if (req.DateFromUtc.HasValue) where.Add("(p.updated_at >= @from OR p.created_at >= @from)");
                if (req.DateToUtc.HasValue) where.Add("(p.updated_at <  @to   OR p.created_at <  @to)");

                var needLbl = labelCodes.Length > 0;
                var needTag = hashtags.Length > 0;

                var joinLbl = needLbl
                    ? @"LEFT JOIN dbo.label_assignments la_p ON la_p.target_type='patient' AND la_p.target_id_guid = p.id
                        LEFT JOIN dbo.labels lp ON lp.id = la_p.label_id"
                    : "/* no labels */";
                var whereLbl = needLbl ? $"AND lp.code IN ({labelCodesIn})" : "";

                var joinTag = needTag
                    ? @"LEFT JOIN dbo.hashtag_links hpl ON hpl.target_type='patient' AND hpl.target_id_guid = p.id
                        LEFT JOIN dbo.hashtags hp ON hp.id = hpl.hashtag_id"
                    : "/* no hashtags */";
                var whereTag = needTag ? $"AND hp.tag IN ({hashtagsIn})" : "";

                var sqlPatients = $@"
SELECT
  CAST('patient' AS nvarchar(20))                 AS [type],
  CONVERT(nvarchar(36), p.id)                     AS [id],
  LTRIM(RTRIM(
    COALESCE(p.first_name, N'') + N' ' +
    COALESCE(p.last_name1, N'') + N' ' +
    COALESCE(p.last_name2, N'')
  ))                                              AS [title],
  LEFT(CAST(p.[description] AS nvarchar(4000)),300) AS [snippet],
  p.updated_at                                    AS [updatedAtUtc],
  p.id AS [patient_id]
FROM dbo.patients p
JOIN dbo.org_members m
  ON m.user_id = p.created_by_user_id AND m.org_id = @org
{joinLbl}
{joinTag}
WHERE {string.Join(" AND ", where)}
{whereLbl}
{whereTag}
";
                sbUnion.AppendLine(sqlPatients);
                sbUnion.AppendLine("UNION ALL");
            }

            /* =========================
                 SUBQUERY: TESTS (test_attempts)
               ========================= */
            if (wantTests)
            {
                // Filtrado por organización vía org_members del paciente (mp) o del asignador (m)
                var where = new List<string> {
                    "(mp.org_id = @org OR m.org_id = @org)"
                };

                if (qText.Length > 0)
                {
                    where.Add(@"
                        (
                        op.opinion_text LIKE ('%' + @q + '%') OR
                        ta.status       LIKE ('%' + @q + '%')
                        )");
                }

                var needLbl = labelCodes.Length > 0;
                var needTag = hashtags.Length > 0;

                var sqlTests = $@"
SELECT
  CAST('test' AS nvarchar(20))         AS [type],
  CONVERT(nvarchar(36), t.id)          AS [id],
  CONVERT(nvarchar(36), t.test_id)     AS [title],
  LEFT(t.any_text, 300)                AS [snippet],
  t.updated_at_utc                     AS [updatedAtUtc],
  t.patient_id AS [patient_id]
FROM (
  SELECT
    ta.id,
    ta.test_id,
    ta.patient_id,
    ta.assigned_by_user_id,
    ta.status,
    COALESCE(op.opinion_text, N'') AS any_text,
    (
      SELECT MAX(x) FROM (VALUES
        (op.updated_at_utc),
        (op.created_at_utc),
        (ta.updated_at),
        (ta.completed_at),
        (ta.started_at),
        (ta.created_at)
      ) AS v(x)
    ) AS updated_at_utc,
    COALESCE(mp.org_id, m.org_id) AS orgIdForAttempt
  FROM dbo.test_attempts ta
  LEFT JOIN dbo.test_attempt_ai_opinions op ON op.attempt_id = ta.id
  LEFT JOIN dbo.patients p                 ON p.id = ta.patient_id
  LEFT JOIN dbo.org_members mp             ON mp.user_id = p.created_by_user_id
  LEFT JOIN dbo.org_members m              ON m.user_id = ta.assigned_by_user_id
  WHERE {string.Join(" AND ", where)}
) t
{(needLbl ? @"LEFT JOIN dbo.label_assignments la_ta ON la_ta.target_type='test_attempt' AND la_ta.target_id_guid = t.id
LEFT JOIN dbo.labels lt ON lt.id = la_ta.label_id" : "/* no labels */")}
{(needTag ? @"LEFT JOIN dbo.hashtag_links hlt ON hlt.target_type='test_attempt' AND hlt.target_id_guid = t.id
LEFT JOIN dbo.hashtags h ON h.id = hlt.hashtag_id" : "/* no hashtags */")}
WHERE 1=1
{(req.DateFromUtc.HasValue ? "AND t.updated_at_utc >= @from" : "")}
{(req.DateToUtc.HasValue ? "AND t.updated_at_utc <  @to" : "")}
{(needLbl ? $"AND lt.code IN ({labelCodesIn})" : "")}
{(needTag ? $"AND h.tag IN ({hashtagsIn})" : "")}
";
                sbUnion.AppendLine(sqlTests);
                sbUnion.AppendLine("UNION ALL");
            }

            /* =========================
                 SUBQUERY: ATTACHMENTS (patient_files)
               ========================= */
            if (wantFiles)
            {
                var where = new List<string> { "pf.org_id = @org", "pf.deleted_at_utc IS NULL" };

                if (qText.Length > 0)
                {
                    where.Add(@"
                        (
                        pf.original_name LIKE ('%' + @q + '%') OR
                        pf.content_type  LIKE ('%' + @q + '%') OR
                        pf.[comment]     LIKE ('%' + @q + '%')
                        )");
                }
                if (req.DateFromUtc.HasValue) where.Add("pf.uploaded_at_utc >= @from");
                if (req.DateToUtc.HasValue) where.Add("pf.uploaded_at_utc <  @to");

                var needLbl = labelCodes.Length > 0;
                var needTag = hashtags.Length > 0;

                var joinLbl = needLbl
                    ? @"INNER JOIN dbo.label_assignments la_f ON la_f.org_id = pf.org_id AND la_f.target_type='attachment' AND la_f.target_id_guid = pf.file_id
                        INNER JOIN dbo.labels lfa ON lfa.id = la_f.label_id AND lfa.org_id = pf.org_id"
                    : "/* no labels */";
                var whereLbl = needLbl ? $"AND lfa.code IN ({labelCodesIn})" : "";

                var joinTag = needTag
                    ? @"INNER JOIN dbo.hashtag_links hlf ON hlf.org_id = pf.org_id AND hlf.target_type='attachment' AND hlf.target_id_guid = pf.file_id
                        INNER JOIN dbo.hashtags h ON h.id = hlf.hashtag_id AND h.org_id = pf.org_id"
                    : "/* no hashtags */";
                var whereTag = needTag ? $"AND h.tag IN ({hashtagsIn})" : "";

                var sqlFiles = $@"
SELECT
  CAST('attachment' AS nvarchar(20))     AS [type],
  CONVERT(nvarchar(36), pf.file_id)      AS [id],
  pf.original_name                       AS [title],
  LEFT(COALESCE(pf.[comment], N''), 300) AS [snippet],
  pf.uploaded_at_utc                     AS [updatedAtUtc],
  pf.patient_id AS [patient_id]
FROM dbo.patient_files pf
{joinLbl}
{joinTag}
WHERE {string.Join(" AND ", where)}
{whereLbl}
{whereTag}
";
                sbUnion.AppendLine(sqlFiles);
                sbUnion.AppendLine("UNION ALL");
            }

            // Quitar el último UNION ALL
            if (sbUnion.Length == 0)
            {
                return new SearchResponseDto
                {
                    Page = page,
                    PageSize = take,
                    Total = 0,
                    Items = new(),
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
            else
            {
                var u = sbUnion.ToString();
                var last = u.LastIndexOf("UNION ALL", StringComparison.OrdinalIgnoreCase);
                if (last >= 0) sbUnion.Remove(last, "UNION ALL".Length);
            }

            // Wrap con total + orden + paginación
            var sql = $@"
WITH unified AS (
{sbUnion}
),
counted AS (
  SELECT COUNT_BIG(*) AS total FROM unified
)
SELECT u.[type], u.[id], u.[title], u.[snippet], u.[updatedAtUtc], u.[patient_id], c.total
FROM unified u
CROSS JOIN counted c
ORDER BY 
  CASE WHEN @q IS NULL THEN 0 ELSE 
    CASE 
      WHEN u.[title]   LIKE ('%' + @q + '%') THEN 0
      WHEN u.[snippet] LIKE ('%' + @q + '%') THEN 1
      ELSE 2
    END
  END,
  u.[updatedAtUtc] DESC
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
";
            prms.Add(new("@skip", SqlDbType.Int) { Value = skip });
            prms.Add(new("@take", SqlDbType.Int) { Value = take });

            var items = new List<SearchResultItemDto>();
            long total = 0;

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using (var cmd = new SqlCommand(sql, cn))
            {
                foreach (var p in prms) cmd.Parameters.Add(p);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    if (total == 0 && !rd.IsDBNull(rd.FieldCount - 1))
                        total = rd.GetInt64(rd.FieldCount - 1);

                    var dto = new SearchResultItemDto
                    {
                        Type = rd.GetString(0),
                        Id = rd.GetString(1),
                        Title = rd.IsDBNull(2) ? null : rd.GetString(2),
                        Snippet = rd.IsDBNull(3) ? null : rd.GetString(3),
                        UpdatedAtUtc = rd.IsDBNull(4) ? (DateTime?)null : rd.GetDateTime(4),
                        Labels = new(),
                        Hashtags = new(),
                        Url = BuildUrl(rd.GetString(0), rd.GetGuid(5).ToString(),  rd.GetString(1))
                    };
                    items.Add(dto);
                }
            }

            return new SearchResponseDto
            {
                Page = page,
                PageSize = take,
                Total = total,
                Items = items,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        public async Task<SearchSuggestResponse> SuggestAsync(Guid orgId, string q, int limit, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            q = (q ?? string.Empty).Trim();

            var tagList = new List<string>();
            var labelList = new List<LabelSuggestDto>();
            var entityList = new List<EntitySuggestDto>();

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            // Hashtags (prefijo)
            if (q.Length > 0)
            {
                const string sqlTags = @"
SELECT TOP (@lim) h.tag
FROM dbo.hashtags h
WHERE h.org_id=@org AND h.tag LIKE (@q + '%')
ORDER BY h.tag ASC;";
                await using (var cmd = new SqlCommand(sqlTags, cn))
                {
                    cmd.Parameters.Add(new("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                    cmd.Parameters.Add(new("@q", SqlDbType.NVarChar, 64) { Value = q.ToLowerInvariant() });
                    cmd.Parameters.Add(new("@lim", SqlDbType.Int) { Value = limit });
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct)) tagList.Add(rd.GetString(0));
                }
            }

            // Labels (prefijo)
            if (q.Length > 0)
            {
                const string sqlLabels = @"
SELECT TOP (@lim) l.id, l.code, l.name, l.color_hex
FROM dbo.labels l
WHERE l.org_id=@org AND (l.code LIKE (@q + '%') OR l.name LIKE (@q + '%'))
ORDER BY l.code ASC;";
                try
                {
                    await using var cmd = new SqlCommand(sqlLabels, cn);
                    cmd.Parameters.Add(new("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                    cmd.Parameters.Add(new("@q", SqlDbType.NVarChar, 128) { Value = q });
                    cmd.Parameters.Add(new("@lim", SqlDbType.Int) { Value = limit });
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                    {
                        labelList.Add(new LabelSuggestDto
                        {
                            Id = rd.GetInt32(0),                // labels.id es INT
                            Code = rd.IsDBNull(1) ? "" : rd.GetString(1),
                            Name = rd.IsDBNull(2) ? "" : rd.GetString(2),
                            ColorHex = rd.IsDBNull(3) ? "#999999" : rd.GetString(3)
                        });
                    }
                }
                catch { /* opcional */ }
            }

            // ===== ENTIDADES (prefijo) =====

            int per = Math.Max(1, limit / 5);

            // Patients
            {
                const string sql = @"
SELECT TOP (@lim)
  CONVERT(nvarchar(36), p.id) AS id,
  LTRIM(RTRIM(COALESCE(p.first_name,'') + ' ' + COALESCE(p.last_name1,'') + ' ' + COALESCE(p.last_name2,''))) AS title
FROM dbo.patients p
JOIN dbo.org_members m ON m.user_id = p.created_by_user_id AND m.org_id = @org
WHERE p.first_name LIKE (@q + '%') OR p.last_name1 LIKE (@q + '%') OR p.identification_number LIKE (@q + '%')
ORDER BY p.updated_at DESC;";
                try
                {
                    await using var cmd = new SqlCommand(sql, cn);
                    cmd.Parameters.Add(new("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                    cmd.Parameters.Add(new("@q", SqlDbType.NVarChar, 200) { Value = q });
                    cmd.Parameters.Add(new("@lim", SqlDbType.Int) { Value = per });
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                        entityList.Add(new EntitySuggestDto { Type = "patient", Id = Guid.Parse(rd.GetString(0)), Title = rd.GetString(1) });
                }
                catch { }
            }

            // Sessions
            {
                const string sql = @"
SELECT TOP (@lim)
  CONVERT(nvarchar(36), s.id) AS id,
  s.title AS title
FROM dbo.patient_sessions s
WHERE s.org_id=@org AND s.deleted_at_utc IS NULL AND (s.title LIKE ('%' + @q + '%') OR s.content_text LIKE ('%' + @q + '%') OR s.ai_tidy_text LIKE ('%' + @q + '%') OR s.ai_opinion_text LIKE ('%' + @q + '%'))
ORDER BY s.updated_at_utc DESC;";
                try
                {
                    await using var cmd = new SqlCommand(sql, cn);
                    cmd.Parameters.Add(new("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                    cmd.Parameters.Add(new("@q", SqlDbType.NVarChar, 200) { Value = q });
                    cmd.Parameters.Add(new("@lim", SqlDbType.Int) { Value = per });
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                        entityList.Add(new EntitySuggestDto { Type = "session", Id = Guid.Parse(rd.GetString(0)), Title = rd.IsDBNull(1) ? "" : rd.GetString(1) });
                }
                catch { }
            }

            // Attachments
            {
                const string sql = @"
SELECT TOP (@lim)
  CONVERT(nvarchar(36), pf.file_id) AS id,
  pf.original_name AS title
FROM dbo.patient_files pf
WHERE pf.org_id=@org AND pf.deleted_at_utc IS NULL AND (pf.original_name LIKE (@q + '%'))
ORDER BY pf.uploaded_at_utc DESC;";
                try
                {
                    await using var cmd = new SqlCommand(sql, cn);
                    cmd.Parameters.Add(new("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                    cmd.Parameters.Add(new("@q", SqlDbType.NVarChar, 200) { Value = q });
                    cmd.Parameters.Add(new("@lim", SqlDbType.Int) { Value = per });
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                        entityList.Add(new EntitySuggestDto { Type = "attachment", Id = Guid.Parse(rd.GetString(0)), Title = rd.IsDBNull(1) ? "" : rd.GetString(1) });
                }
                catch { }
            }

            // Interviews (ahora SÍ filtra por q)
            {
                const string sql = @"
SELECT TOP (@lim)
  CONVERT(nvarchar(36), i.id) AS id,
  CAST(N'Entrevista' AS nvarchar(64)) AS title
FROM dbo.interviews i
JOIN dbo.patients p ON p.id = i.patient_id
JOIN dbo.org_members m ON m.user_id = p.created_by_user_id AND m.org_id=@org
LEFT JOIN dbo.interview_ai_drafts   d ON d.interview_id = i.id
LEFT JOIN dbo.interview_transcripts t ON t.interview_id = i.id
WHERE (ISNULL(i.clinician_diagnosis,'') LIKE (@q + '%')
    OR ISNULL(d.content,'')              LIKE (@q + '%')
    OR ISNULL(t.[text],'')               LIKE (@q + '%'))
ORDER BY i.started_at_utc DESC;";
                try
                {
                    await using var cmd = new SqlCommand(sql, cn);
                    cmd.Parameters.Add(new("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                    cmd.Parameters.Add(new("@q", SqlDbType.NVarChar, 4000) { Value = q });
                    cmd.Parameters.Add(new("@lim", SqlDbType.Int) { Value = per });
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                        entityList.Add(new EntitySuggestDto { Type = "interview", Id = Guid.Parse(rd.GetString(0)), Title = rd.GetString(1) });
                }
                catch { }
            }

            sw.Stop();
            return new SearchSuggestResponse
            {
                Hashtags = tagList.ToArray(),
                Labels = labelList.ToArray(),
                Entities = entityList.ToArray(),
                DurationMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds)
            };
        }

        private static string? BuildUrl(string type, string patientId, string id)
        {
            var lid = (id ?? string.Empty).ToLowerInvariant();
            var lpid = (patientId ?? string.Empty).ToLowerInvariant();

            return type switch
            {
                "session" => $"/app/clinic/pacientes?openPatientId={lpid}&tab=sess&session_id={lid}",
                "interview" => $"/app/clinic/pacientes?openPatientId={lpid}&tab=inter",
                "patient" => $"/app/clinic/pacientes?openPatientId={lpid}&tab=datos",
                "test" => $"/app/clinic/pacientes?openPatientId={lpid}&tab=hist&attempt_id={lid}",
                "attachment" => $"/app/clinic/pacientes?openPatientId={lpid}&tab=adj&attachment_id={lid}",
                _ => null
            };
        }
    }
}
