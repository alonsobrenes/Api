using System.Data;
using Microsoft.Data.SqlClient;
using EPApi.Models;
using Microsoft.Extensions.Configuration;

namespace EPApi.DataAccess
{
    public class UserRepository : IUserRepository
    {
        private readonly string _connectionString;

        public UserRepository(IConfiguration config)
        {
            _connectionString = config.GetConnectionString("Default") ??
                throw new InvalidOperationException("Missing ConnectionStrings:Default");
        }

        public async Task<User?> FindByEmailAsync(string email, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DECLARE @role VARCHAR(25)
                                IF EXISTS(SELECT 1 FROM dbo.users (NOLOCK) WHERE email = @email AND role='admin') 
                                  SET @role = 'admin'
                                ELSE
                                SELECT	@role =  CASE WHEN COUNT(*) = 1 THEN 'editor' ELSE 'viewer' END 
                                FROM dbo.users u (NOLOCK) 
                                   INNER JOIN dbo.org_members m (NOLOCK)
	                                ON u.email = @email
	                                   AND u.id = m.user_id
                                SELECT TOP 1 Id, email, password_hash, @role, created_at
                                FROM dbo.users WHERE email = @email";
            cmd.Parameters.Add(new SqlParameter("@email", SqlDbType.NVarChar, 100) { Value = email });

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            return new User
            {
                Id = reader.GetInt32(0),
                Email = reader.GetString(1),
                PasswordHash = reader.GetString(2),
                Role = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = reader.GetDateTime(4)
            };
        }

        public async Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM dbo.users WHERE email = @email";
            cmd.Parameters.Add(new SqlParameter("@email", SqlDbType.NVarChar, 100) { Value = email });
            var result = await cmd.ExecuteScalarAsync(ct);
            return result != null;
        }

        public async Task<int> CreateAsync(User user, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO dbo.users(email, password_hash, role)
OUTPUT INSERTED.Id
VALUES (@email, @passwordHash, @role);";
            cmd.Parameters.Add(new SqlParameter("@email", SqlDbType.NVarChar, 100) { Value = user.Email });
            cmd.Parameters.Add(new SqlParameter("@passwordHash", SqlDbType.NVarChar, 400) { Value = user.PasswordHash });
            cmd.Parameters.Add(new SqlParameter("@role", SqlDbType.NVarChar, 50) { Value = (object?)user.Role ?? DBNull.Value });

            var insertedId = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(insertedId);
        }

        public async Task<User?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, email, password_hash, role, created_at, avatar_url
FROM dbo.users
WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;

            return new User
            {
                Id = r.GetInt32(0),
                Email = r.GetString(1),
                PasswordHash = r.GetString(2),
                Role = r.GetString(3),
                CreatedAt = r.GetDateTime(4),
                AvatarUrl = r.IsDBNull(5) ? null : r.GetString(5),
            };
        }

        public async Task<bool> UpdateAvatarUrlAsync(int id, string? avatarUrl, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.users
SET avatar_url = @url
WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
            cmd.Parameters.Add(new SqlParameter("@url", SqlDbType.NVarChar, 700) { Value = (object?)avatarUrl ?? DBNull.Value });
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }
    }
}