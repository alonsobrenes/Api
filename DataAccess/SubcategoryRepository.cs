using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using EPApi.Models;

namespace EPApi.DataAccess
{
    public class SubcategoryRepository : ISubcategoryRepository
    {
        private readonly string _cs;
        public SubcategoryRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
        }

        public async Task<(IEnumerable<Subcategory> Items, int Total)> GetPagedAsync(
            int page, int pageSize, string? search, bool? active, int? categoryId, int? disciplineId, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0) pageSize = 20;
            int from = (page - 1) * pageSize + 1;
            int to = from + pageSize - 1;

            var list = new List<Subcategory>();

            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();

            // Usamos join con categories para poder filtrar por disciplineId
            var where = "WHERE 1=1";
            if (!string.IsNullOrWhiteSpace(search))
                where += " AND (sc.name LIKE @s OR sc.code LIKE @s)";
            if (active.HasValue)
                where += " AND sc.is_active = @active";
            if (categoryId.HasValue)
                where += " AND sc.category_id = @catId";
            if (disciplineId.HasValue)
                where += " AND c.discipline_id = @discId";

            cmd.CommandText = $@"
SELECT COUNT(1) AS total
FROM dbo.subcategories sc
INNER JOIN dbo.categories c ON c.id = sc.category_id
{where};

WITH q AS (
  SELECT sc.id, sc.category_id, sc.code, sc.name, sc.description, sc.is_active, sc.created_at, sc.updated_at,
         ROW_NUMBER() OVER (ORDER BY sc.name) AS rn
  FROM dbo.subcategories sc
  INNER JOIN dbo.categories c ON c.id = sc.category_id
  {where}
)
SELECT id, category_id, code, name, description, is_active, created_at, updated_at
FROM q
WHERE rn BETWEEN @from AND @to;";

            if (!string.IsNullOrWhiteSpace(search))
                cmd.Parameters.Add(new SqlParameter("@s", SqlDbType.NVarChar, 200) { Value = $"%{search}%" });
            if (active.HasValue)
                cmd.Parameters.Add(new SqlParameter("@active", SqlDbType.Bit) { Value = active.Value });
            if (categoryId.HasValue)
                cmd.Parameters.Add(new SqlParameter("@catId", SqlDbType.Int) { Value = categoryId.Value });
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

        public async Task<Subcategory?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, category_id, code, name, description, is_active, created_at, updated_at
FROM dbo.subcategories
WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });

            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? Map(r) : null;
        }

        public async Task<int> CreateAsync(Subcategory item, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO dbo.subcategories(category_id, code, name, description, is_active)
OUTPUT INSERTED.id
VALUES (@catId, @code, @name, @desc, @active);";

            cmd.Parameters.Add(new SqlParameter("@catId", SqlDbType.Int) { Value = item.CategoryId });
            cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 32) { Value = item.Code });
            cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 150) { Value = item.Name });
            cmd.Parameters.Add(new SqlParameter("@desc", SqlDbType.NVarChar, 500) { Value = (object?)item.Description ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@active", SqlDbType.Bit) { Value = item.IsActive });

            var id = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(id);
        }

        public async Task<bool> UpdateAsync(int id, Subcategory item, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.subcategories
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
            cmd.CommandText = @"DELETE FROM dbo.subcategories WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }

        private static Subcategory Map(SqlDataReader r) => new Subcategory
        {
            Id = r.GetInt32(0),
            CategoryId = r.GetInt32(1),
            Code = r.GetString(2),
            Name = r.GetString(3),
            Description = r.IsDBNull(4) ? null : r.GetString(4),
            IsActive = r.GetBoolean(5),
            CreatedAt = r.GetDateTime(6),
            UpdatedAt = r.GetDateTime(7)
        };
    }
}
