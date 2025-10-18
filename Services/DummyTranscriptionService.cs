using System.Threading;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace EPApi.Services
{
    /// <summary>
    /// Dummy para desarrollo; reemplazar por Whisper/Vosk/Coqui luego.
    /// </summary>
    public sealed class DummyTranscriptionService : ITranscriptionService
    {
       
        public Task<(string? language, string text, string? wordsJson, long? DurationMs)> TranscribeAsync(string absolutePath, CancellationToken ct = default)
        {
            var text = $"[Transcripción simulada] Archivo: {System.IO.Path.GetFileName(absolutePath)}. Reemplazar por proveedor real.";
            return Task.FromResult<(string?, string, string?, long?)>(("es", text, "", 0));
        }
    }
}
