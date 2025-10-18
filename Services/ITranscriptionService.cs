using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Services
{
    public interface ITranscriptionService
    {
        Task<(string? language, string text, string? wordsJson, long? DurationMs)> TranscribeAsync(string absolutePath, CancellationToken ct = default);
    }
}
