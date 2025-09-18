using System.Data;
using Microsoft.Data.SqlClient;

namespace EPApi.DataAccess
{
    public sealed class HashtagsRepository
    {
        private readonly string _cs;
        public HashtagsRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                  ?? throw new InvalidOperationException("Missing Default connection string");
        }

        /// <summary>
        /// Upsert: devuelve el ID (INT) del hashtag creado o existente para la org.
        /// </summary>
        public async Task<int> UpsertHashtagAsync(Guid orgId, string tag, CancellationToken ct = default)
        {
            // Devuelve el ID del hashtag (creado o existente)
            const string sql = @"
DECLARE @id INT;
SELECT @id = h.id FROM dbo.hashtags h WHERE h.org_id = @org AND h.tag = @tag;
IF @id IS NULL
BEGIN
  INSERT INTO dbo.hashtags(org_id, tag, created_at_utc)
  VALUES (@org, @tag, SYSUTCDATETIME());
  SET @id = SCOPE_IDENTITY();
END
SELECT @id;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@tag", SqlDbType.NVarChar, 64) { Value = tag });
            var id = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(id);
        }

        /// <summary>
        /// Reemplaza las asociaciones del target por exactamente el conjunto recibido (idempotente).
        /// </summary>
        public async Task ReplaceLinksAsync(
            Guid orgId, string targetType, Guid targetId, IReadOnlyCollection<int> hashtagIds, CancellationToken ct = default)
        {
            // Reemplaza las asociaciones del target por exactamente el conjunto recibido (idempotente)
            // 1) Traer actuales
            const string qSel = @"
SELECT hashtag_id
FROM dbo.hashtag_links
WHERE org_id=@org AND target_type=@typ AND target_id_guid=@tid;";

            // 2) Insertar faltantes
            const string qIns = @"
INSERT INTO dbo.hashtag_links(org_id, hashtag_id, target_type, target_id_guid, created_at_utc)
VALUES (@org, @hid, @typ, @tid, SYSUTCDATETIME());";

            // 3) Borrar sobrantes
            const string qDel = @"
DELETE FROM dbo.hashtag_links
WHERE org_id=@org AND target_type=@typ AND target_id_guid=@tid AND hashtag_id=@hid;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            // actuales
            var current = new HashSet<int>();
            await using (var cmd = new SqlCommand(qSel, cn))
            {
                cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@typ", SqlDbType.NVarChar, 32) { Value = targetType });
                cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = targetId });
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) current.Add(rd.GetInt32(0));
            }

            var desired = new HashSet<int>(hashtagIds);
            // inserts
            foreach (var hid in desired)
            {
                if (current.Contains(hid)) continue;
                await using var ci = new SqlCommand(qIns, cn);
                ci.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                ci.Parameters.Add(new SqlParameter("@hid", SqlDbType.Int) { Value = hid });
                ci.Parameters.Add(new SqlParameter("@typ", SqlDbType.NVarChar, 32) { Value = targetType });
                ci.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = targetId });
                await ci.ExecuteNonQueryAsync(ct);
            }
            // deletes
            foreach (var hid in current)
            {
                if (desired.Contains(hid)) continue;
                await using var cd = new SqlCommand(qDel, cn);
                cd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                cd.Parameters.Add(new SqlParameter("@hid", SqlDbType.Int) { Value = hid });
                cd.Parameters.Add(new SqlParameter("@typ", SqlDbType.NVarChar, 32) { Value = targetType });
                cd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = targetId });
                await cd.ExecuteNonQueryAsync(ct);
            }
        }

        /// <summary>
        /// Devuelve la lista de tags (string) asociados a un target (aislado por org).
        /// </summary>
        public async Task<IReadOnlyList<string>> GetTagsForAsync(
            Guid orgId, string targetType, Guid targetId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT H.tag
FROM dbo.hashtag_links L
JOIN dbo.hashtags H ON H.id = L.hashtag_id AND H.org_id = @org
WHERE L.org_id = @org AND L.target_type = @typ AND L.target_id_guid = @tid
ORDER BY H.tag;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@typ", SqlDbType.NVarChar, 32) { Value = targetType });
            cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = targetId });

            var list = new List<string>();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(rd.GetString(0));
            }

            return list;
        }
    }
}
