// DataAccess/LabelsRepository.cs
using System.Data;
using Microsoft.Data.SqlClient;

namespace EPApi.DataAccess
{
    public sealed class LabelsRepository
    {
        private readonly string _cs;
        public LabelsRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                  ?? throw new InvalidOperationException("Missing Default connection string");
        }

        public sealed class LabelRow
        {
            public int Id { get; set; }
            public Guid OrgId { get; set; }
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public string ColorHex { get; set; } = "#000000";
            public bool IsSystem { get; set; }
            public DateTime CreatedAtUtc { get; set; }
        }

        public async Task<IReadOnlyList<LabelRow>> ListAsync(Guid orgId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT id, org_id, code, name, color_hex, is_system, created_at_utc
FROM dbo.labels
WHERE org_id = @org
ORDER BY name;";

            var list = new List<LabelRow>(64);
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new LabelRow
                {
                    Id = rd.GetInt32(0),
                    OrgId = rd.GetGuid(1),
                    Code = rd.GetString(2),
                    Name = rd.GetString(3),
                    ColorHex = rd.GetString(4),
                    IsSystem = rd.GetBoolean(5),
                    CreatedAtUtc = rd.GetDateTime(6)
                });
            }
            return list;
        }

        public async Task<int> CreateAsync(Guid orgId, string code, string name, string colorHex, bool isSystem, CancellationToken ct = default)
        {
            const string sql = @"
INSERT INTO dbo.labels(org_id, code, name, color_hex, is_system, created_at_utc)
OUTPUT INSERTED.id
VALUES (@org, @code, @name, @color, @sys, SYSUTCDATETIME());";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 64) { Value = code });
            cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = name });
            cmd.Parameters.Add(new SqlParameter("@color", SqlDbType.Char, 7) { Value = colorHex });
            cmd.Parameters.Add(new SqlParameter("@sys", SqlDbType.Bit) { Value = isSystem });

            var id = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(id);
        }

        public async Task<LabelRow?> GetByIdAsync(Guid orgId, int id, CancellationToken ct = default)
        {
            const string sql = @"
SELECT id, org_id, code, name, color_hex, is_system, created_at_utc
FROM dbo.labels
WHERE org_id = @org AND id = @id;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                return new LabelRow
                {
                    Id = rd.GetInt32(0),
                    OrgId = rd.GetGuid(1),
                    Code = rd.GetString(2),
                    Name = rd.GetString(3),
                    ColorHex = rd.GetString(4),
                    IsSystem = rd.GetBoolean(5),
                    CreatedAtUtc = rd.GetDateTime(6)
                };
            }
            return null;
        }

        public async Task<bool> UpdateAsync(Guid orgId, int id, string name, string colorHex, CancellationToken ct = default)
        {
            const string sql = @"
UPDATE dbo.labels
SET name = @name, color_hex = @color
WHERE org_id = @org AND id = @id;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
            cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = name });
            cmd.Parameters.Add(new SqlParameter("@color", SqlDbType.Char, 7) { Value = colorHex });

            var n = await cmd.ExecuteNonQueryAsync(ct);
            return n > 0;
        }

        public async Task<long> CountAssignmentsAsync(Guid orgId, int labelId, CancellationToken ct = default)
        {
            const string sql = @"
SELECT COUNT_BIG(1)
FROM dbo.label_assignments
WHERE org_id = @org AND label_id = @lid;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@lid", SqlDbType.Int) { Value = labelId });

            var obj = await cmd.ExecuteScalarAsync(ct);
            return (obj is null) ? 0 : (long)obj;
        }

        public async Task<bool> DeleteAsync(Guid orgId, int id, CancellationToken ct = default)
        {
            const string sql = @"DELETE FROM dbo.labels WHERE org_id = @org AND id = @id;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });

            var n = await cmd.ExecuteNonQueryAsync(ct);
            return n > 0;
        }

    }
}
