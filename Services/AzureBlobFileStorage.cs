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

        public async Task<string> SaveAsync(string relativePath, Stream content, CancellationToken ct = default)
        {
            if (content is null) throw new ArgumentNullException(nameof(content));

            var blobName = NormalizePath(relativePath);
            var blob = _container.GetBlobClient(blobName);

            await blob.UploadAsync(content, overwrite: true, cancellationToken: ct);

            // Devolvemos el nombre normalizado (es lo que se guarda en DB)
            return blobName;
        }

        public async Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct = default)
        {
            var blobName = NormalizePath(relativePath);
            var blob = _container.GetBlobClient(blobName);

            if (!await blob.ExistsAsync(ct))
                return null;

            var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
            // El caller es responsable de disponer el stream
            return response.Value.Content;
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
