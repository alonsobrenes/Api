using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EPApi.Models;
using Microsoft.Extensions.Options;

namespace EPApi.Services.Storage
{
    /// <summary>
    /// Implementación de IFileStorage que usa el sistema de archivos local
    /// debajo de StorageOptions.Local.Root.
    /// </summary>
    public sealed class FileSystemFileStorage : IFileStorage
    {
        private readonly string _root;

        public FileSystemFileStorage(IOptions<StorageOptions> options)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));

            var root = options.Value.Local?.Root;
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("StorageOptions.Local.Root is not configured.");

            _root = root!;
            Directory.CreateDirectory(_root);
        }

        private string GetFullPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("relativePath cannot be null or empty.", nameof(relativePath));

            var safeRelative = relativePath
                .Trim()
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\')
                .Replace('/', Path.DirectorySeparatorChar);

            return Path.Combine(_root, safeRelative);
        }

        public async Task<string> SaveAsync(string relativePath, Stream content, CancellationToken ct = default)
        {
            if (content is null) throw new ArgumentNullException(nameof(content));

            var fullPath = GetFullPath(relativePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await using (var fs = new FileStream(
                fullPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await content.CopyToAsync(fs, 81920, ct);
            }

            // Devolvemos la ruta relativa normalizada con '/'
            var normalized = relativePath
                .Trim()
                .TrimStart('/', '\\')
                .Replace('\\', '/');

            return normalized;
        }

        public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct = default)
        {
            var fullPath = GetFullPath(relativePath);
            if (!File.Exists(fullPath))
                return Task.FromResult<Stream?>(null);

            Stream fs = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            return Task.FromResult<Stream?>(fs);
        }

        public Task<bool> DeleteAsync(string relativePath, CancellationToken ct = default)
        {
            var fullPath = GetFullPath(relativePath);
            if (!File.Exists(fullPath))
                return Task.FromResult(false);

            File.Delete(fullPath);
            return Task.FromResult(true);
        }

        public Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default)
        {
            var fullPath = GetFullPath(relativePath);
            var exists = File.Exists(fullPath);
            return Task.FromResult(exists);
        }
    }
}
