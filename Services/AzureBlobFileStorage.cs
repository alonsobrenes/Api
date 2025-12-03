using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using EPApi.Models;
using Microsoft.Extensions.Options;

namespace EPApi.Services.Storage
{
    /// <summary>
    /// Implementación de IFileStorage usando Azure Blob Storage.
    /// En desarrollo puede apuntar a Azurite.
    /// </summary>
    public sealed class AzureBlobFileStorage : IFileStorage
    {
        private readonly BlobContainerClient _container;
        
        public AzureBlobFileStorage(IOptions<StorageOptions> options)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));
            var opt = options.Value;

            var conn = opt.Blob?.ConnectionString;
            if (string.IsNullOrWhiteSpace(conn))
                throw new InvalidOperationException("StorageOptions.Blob.ConnectionString is not configured.");

            var containerName = opt.Blob?.ContainerName;
            if (string.IsNullOrWhiteSpace(containerName))
                throw new InvalidOperationException("StorageOptions.Blob.ContainerName is not configured.");

            var serviceClient = new BlobServiceClient(conn);
            _container = serviceClient.GetBlobContainerClient(containerName);
            _container.CreateIfNotExists(PublicAccessType.None);
        }

        private static string NormalizePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("relativePath cannot be null or empty.", nameof(relativePath));

            return relativePath
                .Trim()
                .TrimStart('/', '\\')
                .Replace('\\', '/');
        }

        private static string GetContentTypeFromExtension(string? ext)
        {
            if (string.IsNullOrWhiteSpace(ext))
                return "application/octet-stream";

            ext = ext.ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".pdf" => "application/pdf",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".mp4" => "video/mp4",
                ".txt" => "text/plain",
                ".json" => "application/json",
                _ => "application/octet-stream"
            };
        }

        public async Task<string> SaveAsync(string relativePath, Stream content, CancellationToken ct = default)
        {
            if (content is null) throw new ArgumentNullException(nameof(content));

            var blobName = NormalizePath(relativePath);
            var blob = _container.GetBlobClient(blobName);

            if (content.CanSeek)
                content.Position = 0;

            var ext = Path.GetExtension(blobName);
            var contentType = GetContentTypeFromExtension(ext);

            var options = new BlobUploadOptions
            {                
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = contentType
                }                
            };

            await blob.UploadAsync(content, options, ct);
            
            return blobName;
        }

        public async Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct = default)
        {
            var blobName = NormalizePath(relativePath);
            var blob = _container.GetBlobClient(blobName);
            try
            {
                // Si no existe, devolvemos null
                var exists = await blob.ExistsAsync(ct);
                if (!exists.Value)
                    return null;

                var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
                return response.Value.Content;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Si durante el download retorna 404, tratamos como no encontrado
                return null;
            }
            catch (Exception ex)
            {
                // Aquí puedes loguear si tienes ILogger inyectado,
                // pero hacia afuera preferimos NO reventar la API con 500 por un blob corrupto.
                // Por ejemplo:
                

                return null;
            }
            //if (!await blob.ExistsAsync(ct))
            //    return null;

            //var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
            //// El caller es responsable de disponer el stream
            //return response.Value.Content;
        }

        public async Task<bool> DeleteAsync(string relativePath, CancellationToken ct = default)
        {
            var blobName = NormalizePath(relativePath);
            var blob = _container.GetBlobClient(blobName);

            var response = await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
            return response.Value;
        }

        public async Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default)
        {
            var blobName = NormalizePath(relativePath);
            var blob = _container.GetBlobClient(blobName);
            var exists = await blob.ExistsAsync(ct);
            return exists.Value;
        }
    }
}
