using System.Data;
using Microsoft.Data.SqlClient;
using EPApi.Models;
using Microsoft.Extensions.Configuration;

namespace EPApi.DataAccess
{
    public class DisciplineRepository : IDisciplineRepository
    {
        private readonly string _cs;

        public DisciplineRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
        }

        public async Task<(IEnumerable<Discipline> Items, int Total)> GetPagedAsync(
            int page, int pageSize, string? search, bool? active, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 20;
            int from = (page - 1) * pageSize + 1;
            int to = from + pageSize - 1;

            var list = new List<Discipline>();

            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();

            // WHERE dinámico
            var where = "WHERE 1=1";
            if (!string.IsNullOrWhiteSpace(search))
                where += " AND (name LIKE @s OR code LIKE @s)";
            if (active.HasValue)
                where += " AND is_active = @active";

            cmd.CommandText = $@"
SELECT COUNT(1) AS total
FROM dbo.disciplines
{where};

WITH q AS (
  SELECT id, code, name, description, is_active, created_at, updated_at,
         ROW_NUMBER() OVER (ORDER BY name) AS rn
  FROM dbo.disciplines
  {where}
)
SELECT id, code, name, description, is_active, created_at, updated_at
FROM q
WHERE rn BETWEEN @from AND @to;";
            if (!string.IsNullOrWhiteSpace(search))
                cmd.Parameters.Add(new SqlParameter("@s", SqlDbType.NVarChar, 200) { Value = $"%{search}%" });
            if (active.HasValue)
                cmd.Parameters.Add(new SqlParameter("@active", SqlDbType.Bit) { Value = active.Value });
            cmd.Parameters.Add(new SqlParameter("@from", SqlDbType.Int) { Value = from });
            cmd.Parameters.Add(new SqlParameter("@to", SqlDbType.Int) { Value = to });

            await using var r = await cmd.ExecuteReaderAsync(ct);

            int total = 0;
            if (await r.ReadAsync(ct)) total = r.GetInt32(0);

            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(Map(r));

            return (list, total);
        }

        public async Task<Discipline?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, code, name, description, is_active, created_at, updated_at
FROM dbo.disciplines
WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });

            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? Map(r) : null;
        }

        public async Task<int> CreateAsync(Discipline item, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO dbo.disciplines(code, name, description, is_active)
OUTPUT INSERTED.id
VALUES (@code, @name, @desc, @active);";
            cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 32) { Value = item.Code });
            cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 150) { Value = item.Name });
            cmd.Parameters.Add(new SqlParameter("@desc", SqlDbType.NVarChar, 500) { Value = (object?)item.Description ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@active", SqlDbType.Bit) { Value = item.IsActive });

            var id = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(id);
        }

        public async Task<bool> UpdateAsync(int id, Discipline item, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.disciplines
SET name = @name,
    description = @desc,
    is_active = @active,
    updated_at = SYSUTCDATETIME()
WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
            cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 150) { Value = item.Name });
            cmd.Parameters.Add(new SqlParameter("@desc", SqlDbType.NVarChar, 500) { Value = (object?)item.Description ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@active", SqlDbType.Bit) { Value = item.IsActive });

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }

        public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM dbo.disciplines WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }

        private static Discipline Map(SqlDataReader r) => new Discipline
        {
            Id = r.GetInt32(0),
            Code = r.GetString(1),
            Name = r.GetString(2),
            Description = r.IsDBNull(3) ? null : r.GetString(3),
            IsActive = r.GetBoolean(4),
            CreatedAt = r.GetDateTime(5),
            UpdatedAt = r.GetDateTime(6)
        };
    }
}
