using System;

namespace EPApi.Models
{
    /// <summary>
    /// Configuración para archivado de adjuntos (FS + DB).
    /// Sección sugerida en appsettings: "StorageArchive".
    /// </summary>
    public sealed class StorageArchiveOptions
    {
        /// <summary>Subcarpeta bajo Root donde se guardarán archivos archivados. Ej: "archive"</summary>
        public string FolderName { get; set; } = "archive";

        /// <summary>Días que deben transcurrir desde deleted_at_utc para archivar.</summary>
        public int RetentionDays { get; set; } = 30;

        /// <summary>Subcarpeta temporal bajo Root usada durante el movimiento atómico.</summary>
        public string TempFolderName { get; set; } = "_archtmp";

        /// <summary>Tamaño del lote por corrida.</summary>
        public int BatchSize { get; set; } = 100;

        /// <summary>Hora local para ejecutar el job diario (formato HH:mm). Ej: "02:00"</summary>
        public string DailyRunLocalTime { get; set; } = "02:00";
    }
}