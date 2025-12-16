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
                                SELECT TOP 1 Id, email, password_hash, @role, created_at,first_name,last_name1,last_name2,phone,title_prefix,license_number,signature_image_url
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
                CreatedAt = reader.GetDateTime(4),
                FirstName= reader.GetString(5),
                LastName1 = reader.GetString(6),
                LastName2 = reader.GetString(7),
                Phone = reader.GetString(8),
                TitlePrefix = reader.GetString(9),
                LicenseNumber = reader.GetString(10),
                SignatureImageUrl = reader.IsDBNull(11) ? null : reader.GetString(11),
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
SELECT id, email, password_hash, role, created_at, avatar_url,first_name,last_name1,last_name2,phone,title_prefix,license_number,signature_image_url
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
                FirstName = r.GetString(6),
                LastName1 = r.GetString(7),
                LastName2 = r.GetString(8),
                Phone = r.GetString(9),
                TitlePrefix = r.GetString(10),
                LicenseNumber = r.GetString(11),
                SignatureImageUrl = r.IsDBNull(12) ? null : r.GetString(12),
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

        public async Task<bool> UpdateSignatureImageUrlAsync(int id, string? signatureImageUrl, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.users
SET signature_image_url = @url
WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
            cmd.Parameters.Add(new SqlParameter("@url", SqlDbType.NVarChar, 700) { Value = (object?)signatureImageUrl ?? DBNull.Value });
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }

        public async Task UpdateProfileAsync(
    int userId,
    string? firstName,
    string? lastName1,
    string? lastName2,
    string? phone,
    string? titlePrefix,
    string? licenseNumber,
    string? signatureImageUrl,
    CancellationToken ct = default)
        {
            const string sql = @"
UPDATE dbo.users
SET
    first_name = @firstName,
    last_name1 = @lastName1,
    last_name2 = @lastName2,
    phone = @phone,
    title_prefix = @titlePrefix,
    license_number = @licenseNumber,
    signature_image_url = @signatureImageUrl
WHERE id = @id;";

            await using var cn = new SqlConnection(_connectionString);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);

            cmd.Parameters.AddWithValue("@id", userId);
            cmd.Parameters.AddWithValue("@firstName", (object?)firstName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lastName1", (object?)lastName1 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lastName2", (object?)lastName2 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@phone", (object?)phone ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@titlePrefix", (object?)titlePrefix ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@licenseNumber", (object?)licenseNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@signatureImageUrl", (object?)signatureImageUrl ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }

    }
}