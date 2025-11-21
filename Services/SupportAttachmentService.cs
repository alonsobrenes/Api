using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using EPApi.Services.Storage;

namespace EPApi.Services
{
    public sealed class SupportAttachmentInfo
    {
        public Guid Id { get; init; }
        public string FileName { get; init; } = "";
        public string Uri { get; init; } = "";
        public string MimeType { get; init; } = "";
        public int SizeBytes { get; init; }
        public DateTime CreatedAtUtc { get; init; }
    }

    public interface ISupportAttachmentService
    {
        Task<SupportAttachmentInfo> SaveAsync(Guid ticketId, int userId, IFormFile file, CancellationToken ct = default);
        Task<IReadOnlyList<SupportAttachmentInfo>> GetForTicketAsync(Guid ticketId, CancellationToken ct = default);
        Task DeleteAsync(Guid ticketId, Guid attachmentId, CancellationToken ct = default);
        Task<(SupportAttachmentInfo Info, Stream Content)?> OpenReadAsync(Guid ticketId, Guid attachmentId, CancellationToken ct = default);

    }

    public class SupportAttachmentService : ISupportAttachmentService
    {
        private readonly string _cs;
        private readonly string _webRootPath;
        private readonly IConfiguration _cfg;
        private readonly IFileStorage _fileStorage;

        // MIME/EXT permitidos v1
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".pdf"
        };

        public SupportAttachmentService(IConfiguration cfg, IWebHostEnvironment env, IFileStorage fileStorage)
        {
            _cs = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
            _webRootPath = env.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
            _cfg = cfg;
            _fileStorage = fileStorage;
        }

        public async Task<SupportAttachmentInfo> SaveAsync(Guid ticketId, int userId, IFormFile file, CancellationToken ct = default)
        {
            if (file == null || file.Length == 0)
                throw new InvalidOperationException("Archivo vacío.");

            const long maxBytes = 10 * 1024 * 1024; // 10 MB
            if (file.Length > maxBytes)
                throw new InvalidOperationException("El archivo supera el tamaño máximo permitido (10 MB).");

            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(ext) || !AllowedExtensions.Contains(ext))
                throw new InvalidOperationException("Tipo de archivo no permitido. Usa PNG, JPG/JPEG, WEBP o PDF.");

            var attachmentId = Guid.NewGuid();
            var safeFileName = Path.GetFileName(file.FileName);
            var mime = file.ContentType;
            var size = file.Length;

            var storageKey = StoragePathHelper.GetSupportTicketAttachmentPath(ticketId, attachmentId);

            await using (var blobStream = file.OpenReadStream())
            {
                await _fileStorage.SaveAsync(storageKey, blobStream, ct);
            }

            var uri = $"/api/support/tickets/{ticketId:D}/attachments/{attachmentId:D}";


            // Insert en DB
            await using (var conn = new SqlConnection(_cs))
            {
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO dbo.support_attachments
  (id, ticket_id, uploaded_by_user_id, file_name, uri, mime_type, size_bytes)
VALUES
  (@id, @tid, @uid, @fname, @uri, @mime, @size);";
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = attachmentId });
                cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = ticketId });
                cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
                cmd.Parameters.Add(new SqlParameter("@fname", SqlDbType.NVarChar, 260) { Value = file.FileName ?? safeFileName });
                cmd.Parameters.Add(new SqlParameter("@uri", SqlDbType.NVarChar, 600) { Value = uri });
                cmd.Parameters.Add(new SqlParameter("@mime", SqlDbType.NVarChar, 100) { Value = file.ContentType ?? "application/octet-stream" });
                cmd.Parameters.Add(new SqlParameter("@size", SqlDbType.Int) { Value = (int)file.Length });

                await cmd.ExecuteNonQueryAsync(ct);
            }

            return new SupportAttachmentInfo
            {
                Id = attachmentId,
                FileName = file.FileName ?? safeFileName,
                Uri = uri,
                MimeType = file.ContentType ?? "application/octet-stream",
                SizeBytes = (int)file.Length,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        public async Task<(SupportAttachmentInfo Info, Stream Content)?> OpenReadAsync(
    Guid ticketId,
    Guid attachmentId,
    CancellationToken ct = default)
        {
            // 1) Obtener metadata del adjunto
            SupportAttachmentInfo? info = null;
            string? uri = null;

            await using (var conn = new SqlConnection(_cs))
            {
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT id, ticket_id, file_name, uri, mime_type, size_bytes, created_at_utc
FROM dbo.support_attachments
WHERE id = @id AND ticket_id = @tid;";
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = attachmentId });
                cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = ticketId });

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    return null;

                var id = reader.GetGuid(0);
                var tid = reader.GetGuid(1);
                var fileName = reader.GetString(2);
                uri = reader.IsDBNull(3) ? null : reader.GetString(3);
                var mime = reader.IsDBNull(4) ? "application/octet-stream" : reader.GetString(4);
                var size = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                var createdAt = reader.IsDBNull(6) ? DateTime.UtcNow : reader.GetDateTime(6);

                info = new SupportAttachmentInfo
                {
                    Id = id,
                    FileName = fileName,
                    Uri = uri ?? "",
                    MimeType = mime,
                    SizeBytes = size,
                    CreatedAtUtc = createdAt
                };
            }

            // 2) Intentar Blob primero (nuevo esquema)
            var storageKey = StoragePathHelper.GetSupportTicketAttachmentPath(ticketId, attachmentId);
            var blobStream = await _fileStorage.OpenReadAsync(storageKey, ct);
            if (blobStream != null)
            {
                return (info!, blobStream);
            }

            // 3) Fallback: adjuntos legacy en disco (wwwroot/uploads/support/...)
            if (!string.IsNullOrWhiteSpace(uri))
            {
                const string marker = "/uploads/";
                var idx = uri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var relativePath = uri.Substring(idx); // /uploads/support/xxx/yyy.ext

                    var physicalPath = Path.Combine(
                        _webRootPath,
                        relativePath.TrimStart('/').Replace("/", Path.DirectorySeparatorChar.ToString())
                    );

                    if (File.Exists(physicalPath))
                    {
                        var fs = File.OpenRead(physicalPath);
                        return (info!, fs);
                    }
                }
            }

            // 4) No existe en Blob ni en disco
            return null;
        }


        public async Task<IReadOnlyList<SupportAttachmentInfo>> GetForTicketAsync(Guid ticketId, CancellationToken ct = default)
        {
            var list = new List<SupportAttachmentInfo>();
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, file_name, uri, mime_type, size_bytes, created_at_utc
FROM dbo.support_attachments
WHERE ticket_id = @tid
ORDER BY created_at_utc ASC;";
            cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = ticketId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new SupportAttachmentInfo
                {
                    Id = rd.GetGuid(0),
                    FileName = rd.GetString(1),
                    Uri = rd.GetString(2),
                    MimeType = rd.GetString(3),
                    SizeBytes = rd.GetInt32(4),
                    CreatedAtUtc = rd.GetDateTime(5)
                });
            }
            return list;
        }

        public async Task DeleteAsync(Guid ticketId, Guid attachmentId, CancellationToken ct = default)
        {
            string? uri = null;

            await using (var conn = new SqlConnection(_cs))
            {
                await conn.OpenAsync(ct);
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT uri
FROM dbo.support_attachments
WHERE id = @id AND ticket_id = @tid;";
                    cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = attachmentId });
                    cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = ticketId });

                    var result = await cmd.ExecuteScalarAsync(ct);
                    uri = result as string;
                }

                // Borramos el registro SIEMPRE, independientemente del IO
                await using (var cmdDel = conn.CreateCommand())
                {
                    cmdDel.CommandText = @"
DELETE FROM dbo.support_attachments
WHERE id = @id AND ticket_id = @tid;";
                    cmdDel.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = attachmentId });
                    cmdDel.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = ticketId });
                    await cmdDel.ExecuteNonQueryAsync(ct);
                }
            }

            // NUEVO: intentar borrar en Blob/Azurite
            try
            {
                var storageKey = StoragePathHelper.GetSupportTicketAttachmentPath(ticketId, attachmentId);
                await _fileStorage.DeleteAsync(storageKey, ct);
            }
            catch
            {
                // Silencioso: no queremos romper por errores de storage externo
            }

            // Legacy: borrar archivo físico si la URI apunta a /uploads/...
            try
            {
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    const string marker = "/uploads/";
                    var idx = uri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

                    if (idx >= 0)
                    {
                        var relativePath = uri.Substring(idx);  // incluye el / inicial

                        var physicalPath = Path.Combine(
                            _webRootPath,
                            relativePath.TrimStart('/').Replace("/", Path.DirectorySeparatorChar.ToString())
                        );

                        if (File.Exists(physicalPath))
                        {
                            File.Delete(physicalPath);
                        }
                    }
                    // Si no encuentra "/uploads/", asumimos que es una URI nueva (API, etc.) y no hay archivo físico legacy
                }
            }
            catch
            {
                // No romper por errores de IO; el registro ya fue eliminado
            }
        }
    }
}
