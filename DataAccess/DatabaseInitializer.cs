using System.Data;
using Microsoft.Data.SqlClient;
using EPApi.Services;

namespace EPApi.DataAccess
{
    public static class DatabaseInitializer
    {
        public static async Task EnsureCreatedAndSeedAsync(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Missing ConnectionStrings:Default");

            var builder = new SqlConnectionStringBuilder(connectionString);
            var database = builder.InitialCatalog;
            if (string.IsNullOrEmpty(database))
                throw new InvalidOperationException("Connection string must include Initial Catalog/Database.");

            var masterCs = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
            using (var conn = new SqlConnection(masterCs))
            {
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = @db)
BEGIN
    DECLARE @sql nvarchar(max) = 'CREATE DATABASE [' + @db + ']';
    EXEC(@sql);
END";
                cmd.Parameters.Add(new SqlParameter("@db", SqlDbType.NVarChar, 128) { Value = database });
                await cmd.ExecuteNonQueryAsync();
            }

            using (var conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
IF OBJECT_ID('dbo.Users') IS NULL
BEGIN
    CREATE TABLE dbo.Users(
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Email NVARCHAR(100) NOT NULL UNIQUE,
        PasswordHash NVARCHAR(400) NOT NULL,
        Role NVARCHAR(50) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Email = N'demo@demo.com')
BEGIN
    INSERT INTO dbo.users(email, password_hash, role)
    VALUES (N'demo@demo.com', @demoHash, N'admin');
END;
";
                var hasher = new PasswordHasher();
                var hash = hasher.Hash("demo");
                cmd.Parameters.Add(new SqlParameter("@demoHash", SqlDbType.NVarChar, 400) { Value = hash });
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}