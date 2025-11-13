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
    }

    public class SupportAttachmentService : ISupportAttachmentService
    {
        private readonly string _cs;
        private readonly string _webRootPath;
        private readonly IConfiguration _cfg;

        // MIME/EXT permitidos v1
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".pdf"
        };

        public SupportAttachmentService(IConfiguration cfg, IWebHostEnvironment env)
        {
            _cs = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
            _webRootPath = env.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
            _cfg = cfg;
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
            // Ruta física: wwwroot/uploads/support/{ticketIdN}/
            var ticketFolderName = ticketId.ToString("N");
            var relativeFolder = Path.Combine("uploads", "support", ticketFolderName);
            var physicalFolder = Path.Combine(_webRootPath, relativeFolder);
            Directory.CreateDirectory(physicalFolder);

            var safeFileName = attachmentId.ToString("N") + ext.ToLowerInvariant();
            var physicalPath = Path.Combine(physicalFolder, safeFileName);
            var uri = "/" + Path.Combine(relativeFolder, safeFileName).Replace("\\", "/");
            var baseUrl = _cfg["PublicBaseUrl"]; // ej: https://localhost:7250
            var fullUrl = $"{baseUrl}{uri}";

            await using (var stream = new FileStream(physicalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(stream, ct);
            }

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
                cmd.Parameters.Add(new SqlParameter("@uri", SqlDbType.NVarChar, 600) { Value = fullUrl });
                cmd.Parameters.Add(new SqlParameter("@mime", SqlDbType.NVarChar, 100) { Value = file.ContentType ?? "application/octet-stream" });
                cmd.Parameters.Add(new SqlParameter("@size", SqlDbType.Int) { Value = (int)file.Length });

                await cmd.ExecuteNonQueryAsync(ct);
            }

            return new SupportAttachmentInfo
            {
                Id = attachmentId,
                FileName = file.FileName ?? safeFileName,
                Uri = fullUrl,
                MimeType = file.ContentType ?? "application/octet-stream",
                SizeBytes = (int)file.Length,
                CreatedAtUtc = DateTime.UtcNow
            };
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
            // 1) Buscar el attachment y su URI
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

                    var obj = await cmd.ExecuteScalarAsync(ct);
                    if (obj == null || obj == DBNull.Value)
                    {
                        // No existe o no pertenece a ese ticket
                        throw new InvalidOperationException("El adjunto no existe o no pertenece a este ticket.");
                    }

                    uri = (string)obj;
                }

                // 2) Borrar la fila
                await using (var cmd2 = conn.CreateCommand())
                {
                    cmd2.CommandText = @"
DELETE FROM dbo.support_attachments
WHERE id = @id AND ticket_id = @tid;";
                    cmd2.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = attachmentId });
                    cmd2.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = ticketId });

                    await cmd2.ExecuteNonQueryAsync(ct);
                }
            }

            // 3) Intentar borrar el archivo físico (best-effort)
            try
            {
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(uri))
                        {
                            // uri puede ser:
                            //   a) /uploads/support/...
                            //   b) https://localhost:53793/uploads/support/...
                            //
                            // Buscamos dónde empieza "/uploads/"

                            const string marker = "/uploads/";
                            var idx = uri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

                            if (idx >= 0)
                            {
                                // Tomamos solo "/uploads/support/xxx/yyy.ext"
                                var relativePath = uri.Substring(idx);  // incluye el / inicial

                                // Convertir a ruta física:
                                // wwwroot/uploads/support/xxx/yyy.ext
                                var physicalPath = Path.Combine(
                                    _webRootPath,
                                    relativePath.TrimStart('/').Replace("/", Path.DirectorySeparatorChar.ToString())
                                );

                                if (File.Exists(physicalPath))
                                {
                                    File.Delete(physicalPath);
                                }
                            }
                            // Si no encuentra "/uploads/", no borramos (URL incorrecto o storage externo)
                        }
                    }
                    catch
                    {
                        // Silencioso. No debe fallar el delete por IO.
                    }

                }
            }
            catch
            {
                // No romper por errores de IO; el registro ya fue eliminado
            }
        }

    }
}
