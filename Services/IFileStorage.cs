using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Services.Storage
{
    /// <summary>
    /// Abstracción de almacenamiento de archivos a bajo nivel.
    /// Trabaja siempre con rutas relativas (ej. "org/{orgId}/patient/{patientId}/{fileId}").
    /// Implementaciones típicas: sistema de archivos local, Azure Blob, S3, etc.
    /// </summary>
    public interface IFileStorage
    {
        /// <summary>
        /// Guarda un archivo en la ruta relativa indicada.
        /// Debe devolver la ruta relativa normalizada (con slashes '/')
        /// que será la que se persista en la base de datos.
        /// </summary>
        Task<string> SaveAsync(string relativePath, Stream content, CancellationToken ct = default);

        /// <summary>
        /// Abre un archivo para lectura. Devuelve null si no existe.
        /// El caller es responsable de disponer el stream.
        /// </summary>
        Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct = default);

        /// <summary>
        /// Elimina el archivo si existe. Devuelve true si se eliminó; false si no existía.
        /// </summary>
        Task<bool> DeleteAsync(string relativePath, CancellationToken ct = default);

        /// <summary>
        /// Verifica si el archivo existe en el almacenamiento.
        /// </summary>
        Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default);
    }
}
