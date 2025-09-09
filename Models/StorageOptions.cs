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

        public sealed class LocalOptions
        {
            public string Root { get; set; } = "./data/storage";
        }
    }
}
