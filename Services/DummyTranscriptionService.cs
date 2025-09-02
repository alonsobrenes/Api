using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Services
{
    /// <summary>
    /// Dummy para desarrollo; reemplazar por Whisper/Vosk/Coqui luego.
    /// </summary>
    public sealed class DummyTranscriptionService : ITranscriptionService
    {
        public Task<(string? language, string text, string? wordsJson)> TranscribeAsync(string absolutePath, CancellationToken ct = default)
        {
            var text = $"[Transcripción simulada] Archivo: {System.IO.Path.GetFileName(absolutePath)}. Reemplazar por proveedor real.";
            return Task.FromResult<(string?, string, string?)>(("es", text, null));
        }
    }
}
