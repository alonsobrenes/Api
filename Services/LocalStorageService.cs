using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EPApi.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace EPApi.Services.Storage
{
    public sealed class LocalStorageService : IStorageService
    {
        private readonly string _cs;
        private readonly StorageOptions _opt;

        public LocalStorageService(IConfiguration cfg, IOptions<StorageOptions> options)
        {
            _cs = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing Default connection string");
            _opt = options.Value;
        }

        private static string MakeSafeFileName(string input)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                sb.Append(invalid.Contains(ch) ? '_' : ch);
            }
            var safe = sb.ToString();
            // Keep it short to avoid too long paths on Windows
            return safe.Length > 140 ? safe.Substring(0, 140) : safe;
        }

        private static long GiB(int gb) => (long)gb * 1024L * 1024L * 1024L;

        public async Task<int?> GetStorageLimitGbAsync(Guid orgId, CancellationToken ct)
        {
            const string SQL = @"SELECT e.limit_value
FROM dbo.entitlements e
WHERE e.org_id = @org AND e.feature_code = N'storage.gb';";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });

            var res = await cmd.ExecuteScalarAsync(ct);
            if (res == null || res == DBNull.Value) return null;
            return Convert.ToInt32(res, CultureInfo.InvariantCulture);
        }

        public async Task<long?> GetOrgUsedBytesAsync(Guid orgId, CancellationToken ct)
        {
            long usedBytes = 0;
            const string SQL = "SELECT used_bytes FROM dbo.org_storage WITH (UPDLOCK, HOLDLOCK) WHERE org_id = @org;";
            
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });

            var obj = await cmd.ExecuteScalarAsync(ct);
            usedBytes = (obj == null || obj == DBNull.Value) ? 0 : Convert.ToInt64(obj, CultureInfo.InvariantCulture);

            return usedBytes;            
        }

        private async Task EnsureOrgStorageRowAsync(SqlConnection cn, SqlTransaction tx, Guid orgId, CancellationToken ct)
        {
            const string SQL = @"IF NOT EXISTS (SELECT 1 FROM dbo.org_storage WITH (UPDLOCK, HOLDLOCK) WHERE org_id = @org)
BEGIN
  INSERT INTO dbo.org_storage(org_id, used_bytes, updated_at_utc)
  VALUES(@org, 0, SYSUTCDATETIME());
END";
            await using var cmd = new SqlCommand(SQL, cn, tx);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<(Guid fileId, long bytes)> SaveAsync(
            Guid orgId, Guid patientId,
            Stream content, string contentType, string originalName,
            string? comment, int? uploadedByUserId,
            CancellationToken ct)
        {
            if (_opt.Provider != "Local")
                throw new NotSupportedException("Only Local provider is implemented.");

            // Determine size and hash while writing to a temp file
            string root = _opt.Local.Root;
            Directory.CreateDirectory(root);

            // Validate content type early
            if (_opt.AllowedContentTypes?.Count > 0 && !_opt.AllowedContentTypes.Contains(contentType))
                throw new InvalidOperationException("Unsupported content type");

            // Copy stream to a temp file to know exact final size and sha256
            string tempDir = Path.Combine(root, "_tmp");
            Directory.CreateDirectory(tempDir);
            string tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));

            long totalBytes = 0;
            byte[] shaHash;
            using (var sha = SHA256.Create())
            {
                await using var outStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using (var cs = new CryptoStream(outStream, sha, CryptoStreamMode.Write, leaveOpen: true))
                {
                    await content.CopyToAsync(cs, 81920, ct);
                    // finalize hash before reading sha.Hash
                    cs.FlushFinalBlock();
                }

                // If stream isn't seekable, just copy
                //await content.CopyToAsync(cs, 81920, ct);
                //await cs.FlushAsync(ct);
                await outStream.FlushAsync(ct);
                totalBytes = new FileInfo(tempPath).Length;
                shaHash = sha.Hash ?? Array.Empty<byte>();
            }

            if (_opt.MaxFileSizeMB > 0 && totalBytes > (long)_opt.MaxFileSizeMB * 1024L * 1024L)
            {
                File.Delete(tempPath);
                throw new IOException("File too large");
            }

            // Get entitlement
            var limitGb = await GetStorageLimitGbAsync(orgId, ct);
            if (limitGb is null)
            {
                File.Delete(tempPath);
                throw new UnauthorizedAccessException("No storage entitlement for org");
            }
            var allowedBytes = GiB(limitGb.Value);

            var fileId = Guid.NewGuid();
            var safeName = MakeSafeFileName(originalName);
            var relativeKey = Path.Combine("org", orgId.ToString("N"), "patient", patientId.ToString("N"), fileId.ToString("N"));
            var absolutePath = Path.Combine(root, relativeKey);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            try
            {
                await EnsureOrgStorageRowAsync(cn, (SqlTransaction)tx, orgId, ct);

                // Validate quota
                long usedBytes = 0;
                const string Q1 = "SELECT used_bytes FROM dbo.org_storage WITH (UPDLOCK, HOLDLOCK) WHERE org_id = @org;";
                await using (var cmd = new SqlCommand(Q1, cn, (SqlTransaction)tx))
                {
                    cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                    var obj = await cmd.ExecuteScalarAsync(ct);
                    usedBytes = (obj == null || obj == DBNull.Value) ? 0 : Convert.ToInt64(obj, CultureInfo.InvariantCulture);
                }

                if (usedBytes + totalBytes > allowedBytes)
                {
                    throw new InvalidOperationException("Quota exceeded");
                }

                // Move temp to final
                File.Move(tempPath, absolutePath);

                // Insert metadata
                const string INS = @"
INSERT INTO dbo.patient_files
(file_id, org_id, patient_id, original_name, content_type, byte_size,
 storage_provider, storage_key, sha256_hex, comment, uploaded_by_user, uploaded_at_utc, deleted_at_utc)
VALUES (@id, @org, @pat, @name, @ctype, @bytes,
        N'Local', @key, @sha, @comment, @uid, SYSUTCDATETIME(), NULL);";
                await using (var cmd = new SqlCommand(INS, cn, (SqlTransaction)tx))
                {
                    cmd.Parameters.AddRange(new[]
                    {
                        new SqlParameter("@id", SqlDbType.UniqueIdentifier){ Value = fileId },
                        new SqlParameter("@org", SqlDbType.UniqueIdentifier){ Value = orgId },
                        new SqlParameter("@pat", SqlDbType.UniqueIdentifier){ Value = patientId },
                        new SqlParameter("@name", SqlDbType.NVarChar, 256){ Value = safeName },
                        new SqlParameter("@ctype", SqlDbType.NVarChar, 100){ Value = contentType },
                        new SqlParameter("@bytes", SqlDbType.BigInt){ Value = totalBytes },
                        new SqlParameter("@key", SqlDbType.NVarChar, 400){ Value = relativeKey.Replace('\\','/') },
                        new SqlParameter("@sha", SqlDbType.Char, 64){ Value = BitConverter.ToString(shaHash).Replace("-", "").ToLowerInvariant() },
                        new SqlParameter("@comment", SqlDbType.NVarChar, 500){ Value = (object?)comment ?? DBNull.Value },
                        new SqlParameter("@uid", SqlDbType.Int){ Value = (object?)uploadedByUserId ?? DBNull.Value },
                    });
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                // Increment quota
                const string UPD = @"UPDATE dbo.org_storage SET used_bytes = used_bytes + @bytes, updated_at_utc = SYSUTCDATETIME() WHERE org_id = @org;";
                await using (var cmd = new SqlCommand(UPD, cn, (SqlTransaction)tx))
                {
                    cmd.Parameters.Add(new SqlParameter("@bytes", SqlDbType.BigInt) { Value = totalBytes });
                    cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                return (fileId, totalBytes);
            }
            catch
            {
                try
                {
                    await tx.RollbackAsync(ct);
                }
                catch { /* ignore */ }
                // Cleanup temp & final if needed
                if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { }
                if (File.Exists(absolutePath)) try { File.Delete(absolutePath); } catch { }
                throw;
            }
        }

        public async Task<(Stream content, string contentType, string downloadName)> OpenReadAsync(Guid fileId, CancellationToken ct)
        {
            const string SQL = @"SELECT storage_key, content_type, original_name
FROM dbo.patient_files
WHERE file_id = @id AND deleted_at_utc IS NULL;";

            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = fileId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct))
                throw new FileNotFoundException("File not found");

            var key = rd.GetString(0);
            var ctype = rd.GetString(1);
            var name = rd.GetString(2);

            var path = Path.Combine(_opt.Local.Root, key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                throw new FileNotFoundException("File content missing");

            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return (fs, ctype, name);
        }

        public async Task<IReadOnlyList<PatientFileDto>> ListAsync(Guid orgId, Guid patientId, bool isOwner, CancellationToken ct)
        {
            const string SQL = @"SELECT file_id, original_name, content_type, byte_size, uploaded_at_utc, comment, deleted_at_utc
FROM dbo.patient_files
WHERE patient_id = @pat
     AND (@isOwner = 1 OR org_id = @org)
ORDER BY uploaded_at_utc DESC;";

            var list = new List<PatientFileDto>();
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var cmd = new SqlCommand(SQL, cn);
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
            cmd.Parameters.Add(new SqlParameter("@pat", SqlDbType.UniqueIdentifier) { Value = patientId });
            cmd.Parameters.AddWithValue("@isOwner", isOwner ? 1 : 0);

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new PatientFileDto(
                    rd.GetGuid(0),
                    rd.GetString(1),
                    rd.GetString(2),
                    rd.GetInt64(3),
                    rd.GetDateTime(4),
                    rd.IsDBNull(5) ? null : rd.GetString(5),
                    !rd.IsDBNull(6)
                ));
            }

            return list;
        }

        public async Task<bool> SoftDeleteAsync(Guid fileId, Guid orgId, int? deletedByUserId, CancellationToken ct)
        {
            const string SQL = @"
DECLARE @bytes bigint, @already bit;

-- Asegura que el file exista y pertenezca a la org del solicitante
SELECT TOP 1
  @bytes   = byte_size,
  @already = CASE WHEN deleted_at_utc IS NULL THEN 0 ELSE 1 END
FROM dbo.patient_files WITH (UPDLOCK, HOLDLOCK)
WHERE file_id = @id AND org_id = @org;

IF @bytes IS NULL
    RETURN;  -- no existe o no es de esta org

IF @already = 1
    RETURN;  -- ya estaba borrado (idempotente)

UPDATE dbo.patient_files
SET deleted_at_utc   = SYSUTCDATETIME(),
    deleted_by_user_id = @uid
WHERE file_id = @id AND org_id = @org AND deleted_at_utc IS NULL;

UPDATE dbo.org_storage
SET used_bytes     = CASE WHEN used_bytes >= @bytes THEN used_bytes - @bytes ELSE 0 END,
    updated_at_utc = SYSUTCDATETIME()
WHERE org_id = @org;
";
            await using var cn = new SqlConnection(_cs);
            await cn.OpenAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                await using var cmd = new SqlCommand(SQL, cn, (SqlTransaction)tx);
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = fileId });
                cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });
                cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = (object?)deletedByUserId ?? DBNull.Value });

                var rows = await cmd.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
                // If there was no matching row, rows may be 0
                return rows > 0;
            }
            catch
            {
                try { await tx.RollbackAsync(ct); } catch { }
                throw;
            }
        }
    }
}
