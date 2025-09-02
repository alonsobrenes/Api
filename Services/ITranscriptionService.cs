using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Services
{
    public interface ITranscriptionService
    {
        /// <summary>Transcribe un archivo en absolutePath y devuelve (language, text, wordsJson?)</summary>
        Task<(string? language, string text, string? wordsJson)> TranscribeAsync(string absolutePath, CancellationToken ct = default);
    }
}
