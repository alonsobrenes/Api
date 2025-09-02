using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using EPApi.Models;

namespace EPApi.DataAccess
{
    public class CategoryRepository : ICategoryRepository
    {
        private readonly string _cs;
        public CategoryRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
        }

        public async Task<(IEnumerable<Category> Items, int Total)> GetPagedAsync(
            int page, int pageSize, string? search, bool? active, int? disciplineId, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 20;
            int from = (page - 1) * pageSize + 1;
            int to = from + pageSize - 1;

            var list = new List<Category>();

            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();

            var where = "WHERE 1=1";
            if (!string.IsNullOrWhiteSpace(search))
                where += " AND (c.name LIKE @s OR c.code LIKE @s)";
            if (active.HasValue)
                where += " AND c.is_active = @active";
            if (disciplineId.HasValue)
                where += " AND c.discipline_id = @discId";

            cmd.CommandText = $@"
SELECT COUNT(1) AS total
FROM dbo.categories c
{where};

WITH q AS (
  SELECT c.id, c.discipline_id, c.code, c.name, c.description, c.is_active, c.created_at, c.updated_at,
         ROW_NUMBER() OVER (ORDER BY c.name) AS rn
  FROM dbo.categories c
  {where}
)
SELECT id, discipline_id, code, name, description, is_active, created_at, updated_at
FROM q
WHERE rn BETWEEN @from AND @to;";

            if (!string.IsNullOrWhiteSpace(search))
                cmd.Parameters.Add(new SqlParameter("@s", SqlDbType.NVarChar, 200) { Value = $"%{search}%" });
            if (active.HasValue)
                cmd.Parameters.Add(new SqlParameter("@active", SqlDbType.Bit) { Value = active.Value });
            if (disciplineId.HasValue)
                cmd.Parameters.Add(new SqlParameter("@discId", SqlDbType.Int) { Value = disciplineId.Value });
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

        public async Task<Category?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, discipline_id, code, name, description, is_active, created_at, updated_at
FROM dbo.categories
WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });

            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? Map(r) : null;
        }

        public async Task<int> CreateAsync(Category item, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO dbo.categories(discipline_id, code, name, description, is_active)
OUTPUT INSERTED.id
VALUES (@discId, @code, @name, @desc, @active);";

            cmd.Parameters.Add(new SqlParameter("@discId", SqlDbType.Int) { Value = item.DisciplineId });
            cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 32) { Value = item.Code });
            cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 150) { Value = item.Name });
            cmd.Parameters.Add(new SqlParameter("@desc", SqlDbType.NVarChar, 500) { Value = (object?)item.Description ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@active", SqlDbType.Bit) { Value = item.IsActive });

            var id = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(id);
        }

        public async Task<bool> UpdateAsync(int id, Category item, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.categories
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
            cmd.CommandText = @"DELETE FROM dbo.categories WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }

        private static Category Map(SqlDataReader r) => new Category
        {
            Id = r.GetInt32(0),
            DisciplineId = r.GetInt32(1),
            Code = r.GetString(2),
            Name = r.GetString(3),
            Description = r.IsDBNull(4) ? null : r.GetString(4),
            IsActive = r.GetBoolean(5),
            CreatedAt = r.GetDateTime(6),
            UpdatedAt = r.GetDateTime(7)
        };
    }
}
