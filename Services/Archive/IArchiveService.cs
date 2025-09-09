using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Services.Archive
{
    public interface IArchiveService
    {
        /// <summary>
        /// Ejecuta una corrida de archivado (un lote).
        /// Devuelve (okCount, failCount).
        /// </summary>
        Task<(int ok, int fail)> RunOnceAsync(CancellationToken ct);
    }
}
