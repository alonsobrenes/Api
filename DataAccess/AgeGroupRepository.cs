using System.Data;
using Microsoft.Data.SqlClient;
using EPApi.Models;

namespace EPApi.DataAccess
{
    public sealed class AgeGroupRepository : IAgeGroupRepository
    {
        private readonly string _cs;

        public AgeGroupRepository(IConfiguration cfg)
        {
            // Asegúrate que tu appsettings.json tenga "ConnectionStrings:Default"
            _cs = cfg.GetConnectionString("Default");
        }

        public async Task<IReadOnlyList<AgeGroupRow>> GetAllAsync(bool includeInactive, CancellationToken ct = default)
        {
            const string sql = @"
SELECT id, code, name, description, age_min, age_max, is_active, created_at, updated_at
FROM dbo.age_groups
WHERE (@all = 1) OR (is_active = 1)
ORDER BY name;";

            var list = new List<AgeGroupRow>(16);

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@all", SqlDbType.Bit) { Value = includeInactive });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new AgeGroupRow
                {
                    Id = rd.GetGuid(0),
                    Code = rd.GetString(1),
                    Name = rd.GetString(2),
                    Description = rd.IsDBNull(3) ? null : rd.GetString(3),
                    AgeMin = rd.IsDBNull(4) ? (byte?)null : rd.GetByte(4),
                    AgeMax = rd.IsDBNull(5) ? (byte?)null : rd.GetByte(5),
                    IsActive = rd.GetBoolean(6),
                    CreatedAt = rd.GetDateTime(7),
                    UpdatedAt = rd.GetDateTime(8),
                });
            }

            return list;
        }

        public async Task<AgeGroupRow?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            const string sql = @"
SELECT id, code, name, description, age_min, age_max, is_active, created_at, updated_at
FROM dbo.age_groups
WHERE id = @id;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                return new AgeGroupRow
                {
                    Id = rd.GetGuid(0),
                    Code = rd.GetString(1),
                    Name = rd.GetString(2),
                    Description = rd.IsDBNull(3) ? null : rd.GetString(3),
                    AgeMin = rd.IsDBNull(4) ? (byte?)null : rd.GetByte(4),
                    AgeMax = rd.IsDBNull(5) ? (byte?)null : rd.GetByte(5),
                    IsActive = rd.GetBoolean(6),
                    CreatedAt = rd.GetDateTime(7),
                    UpdatedAt = rd.GetDateTime(8),
                };
            }

            return null;
        }
    }
}
