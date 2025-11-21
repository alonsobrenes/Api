using System.Collections.Generic;

namespace EPApi.Models
{
    public sealed class StorageOptions
    {
        public string Provider { get; set; } = "Local";

        public LocalOptions Local { get; set; } = new LocalOptions();

        public int MaxFileSizeMB { get; set; } = 25;

        public List<string> AllowedContentTypes { get; set; } = new List<string>
        {
            "application/pdf",
            "image/png",
            "image/jpeg"
        };

        public BlobStorageOptions Blob { get; set; } = new();

        public sealed class LocalOptions
        {
            public string Root { get; set; } = "./data/storage";
        }

        public sealed class BlobStorageOptions
        {
            /// <summary>
            /// Connection string de Azure Storage o Azurite.
            /// </summary>
            public string? ConnectionString { get; set; }

            /// <summary>
            /// Nombre del contenedor donde se guardarán los archivos (ej: "files").
            /// </summary>
            public string ContainerName { get; set; } = "files";
        }

    }
}
