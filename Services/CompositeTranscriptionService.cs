using EPApi.Services;
using static EPApi.Controllers.ClinicianInterviewsController;

public sealed class CompositeTranscriptionService : ITranscriptionService
{
    private readonly ITranscriptionService _primary;   // Whisper
    private readonly ITranscriptionService _fallback;  // Dummy o Local

    public CompositeTranscriptionService(ITranscriptionService primary, ITranscriptionService fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public async Task<(string? language, string text, string? wordsJson, long? DurationMs)> TranscribeAsync(string absolutePath, CancellationToken ct = default)
    {
        try
        {
            return await _primary.TranscribeAsync(absolutePath, ct);
        }
        catch (RateLimitException)
        {
            // Fallback silencioso (sin marcar en el texto)
            var (lang, text, words, durationMs) = await _fallback.TranscribeAsync(absolutePath, ct);
            return (lang, text, words, durationMs);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("429"))
        {
            var (lang, text, words, durationMs) = await _fallback.TranscribeAsync(absolutePath, ct);
            return (lang, text, words, durationMs);
        }
    }
}
