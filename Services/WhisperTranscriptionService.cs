// Services/WhisperTranscriptionService.cs
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ConstrainedExecution;
using TagLib;
using static EPApi.Controllers.ClinicianInterviewsController;

namespace EPApi.Services
{
    public sealed record TranscriptionResult(string? Language, string Text, string? WordsJson, long? DurationMs);

    public sealed class WhisperTranscriptionService : ITranscriptionService
    {
        private readonly IHttpClientFactory _http;
        private readonly string _apiKey;
        private readonly Random _rng = new();

        public WhisperTranscriptionService(IHttpClientFactory http, IConfiguration cfg)
        {
            _http = http;

            
            _apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                      ?? throw new InvalidOperationException("Missing OpenAI API key");
        }

        private static bool IsLikelyNoSpeech(long? durationMs, string text)
        {
            var trimmed = (text ?? string.Empty).Trim();

            // Si no hay nada de texto, claramente "no speech"
            if (string.IsNullOrEmpty(trimmed))
                return true;

            // Si el audio dura bastante y el texto es ultra corto (ej: "sí", "no")
            // este es un caso delicado. Vamos a ser MUY conservadores:
            // solo lo marcamos como sospechoso si dura al menos 5s y el texto es de 1-2 caracteres.
            if (durationMs.HasValue && durationMs.Value >= 5000 && trimmed.Length <= 2)
                return true;

            return false;
        }


        private static string SanitizeTranscription(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text ?? string.Empty;

            var cleaned = text;

            string[] tails =
            {
        "Subtítulos realizados por la comunidad de Amara.org",
        "Subtitulos realizados por la comunidad de Amara.org",
        "¡Gracias por ver el video y por compartirlo con tus amigos!",
        "¡Gracias por ver el video!"
    };

            foreach (var tail in tails)
            {
                var idx = cleaned.IndexOf(tail, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    cleaned = cleaned[..idx].TrimEnd();
                }
            }

            while (cleaned.EndsWith(".") || cleaned.EndsWith("…"))
            {
                // si queda una frase de verdad, dejamos uno solo, si no, quitamos todos
                var trimmed = cleaned.TrimEnd('.', '…');
                if (trimmed.Length == 0)
                {
                    cleaned = string.Empty;
                    break;
                }

                cleaned = trimmed;
            }

            // Opcional: limpiar espacios y saltos extra al final
            return cleaned.TrimEnd();
        }


        public async Task<(string? language, string text, string? wordsJson, long? DurationMs)> TranscribeAsync(string absolutePath, CancellationToken ct = default)
        {
            var client = _http.CreateClient();
            client.BaseAddress = new Uri("https://api.openai.com/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            client.Timeout = Timeout.InfiniteTimeSpan; 
            
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(20));

            // 3 intentos con backoff exponencial + jitter
            var attempts = 0;
            var delayBase = TimeSpan.FromSeconds(2);

            while (true)
            {
                attempts++;
                using var form = new MultipartFormDataContent
                {
                    { new StringContent("whisper-1"), "model" },
                    { new StringContent("es"), "language" },
                    { new StringContent("0"), "temperature" }
                };

                var fileName = Path.GetFileName(absolutePath);
                var mime = GetMimeFromExtension(Path.GetExtension(absolutePath));
                await using var fs = System.IO.File.OpenRead(absolutePath);
                var fileContent = new StreamContent(fs);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(mime);

                form.Add(fileContent, "file", fileName);
                //form.Add(new StringContent("es"), "language");

                using var resp = await client.PostAsync("v1/audio/transcriptions", form, timeoutCts.Token);
                var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);

                long? durationMs = null;
                try
                {
                    using var t = TagLib.File.Create(absolutePath);
                    var ms = (long)Math.Round(t.Properties.Duration.TotalMilliseconds);
                    if (ms > 0) durationMs = ms;
                }
                catch
                {
                    durationMs = null;
                }

                if (resp.IsSuccessStatusCode)
                {
                    var json = System.Text.Json.JsonDocument.Parse(body).RootElement;
                    var text = json.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    var cleaned = SanitizeTranscription(text);

                    if (IsLikelyNoSpeech(durationMs, cleaned))
                    {                        
                        return ("es", string.Empty, null, durationMs);
                    }

                    return ("es", cleaned, null, durationMs);
                }

                // Manejo amable de rate limit
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    int? retryAfter = null;
                    if (resp.Headers.TryGetValues("Retry-After", out var vals) && int.TryParse(vals.FirstOrDefault(), out var sec))
                        retryAfter = sec;

                    throw new RateLimitException("Servicio de transcripción saturado (429).", retryAfter);

                   

                    //if (attempts >= 3) {
                       

                    //    // headers típicos
                    //    resp.Headers.TryGetValues("Retry-After", out var ra);
                    //    resp.Headers.TryGetValues("x-ratelimit-remaining-requests", out var rr);
                    //    resp.Headers.TryGetValues("x-ratelimit-reset-requests", out var rrs);

                    //    Console.Error.WriteLine($"[Whisper 429] body={body}");
                    //    Console.Error.WriteLine($"Retry-After={ra?.FirstOrDefault()} remaining-req={rr?.FirstOrDefault()} reset-req={rrs?.FirstOrDefault()}");
                    //    throw new RateLimitException($"Servicio de transcripción saturado (429).", retryAfter);
                    //}
                    

                    //// Espera sugerida o backoff exponencial + jitter
                    //var wait = retryAfter.HasValue
                    //    ? TimeSpan.FromSeconds(retryAfter.Value)
                    //    : TimeSpan.FromMilliseconds(delayBase.TotalMilliseconds * Math.Pow(2, attempts - 1) + _rng.Next(200, 800));

                    //await Task.Delay(wait, ct);
                    //continue;
                }

                // Errores informativos comunes
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    throw new HttpRequestException("OpenAI: API key inválida o ausente (401).");
                if (resp.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                    throw new HttpRequestException("Archivo demasiado grande para la API (413).");

                throw new HttpRequestException($"OpenAI error {(int)resp.StatusCode}: {body}");
            }
        }

        private static string GetMimeFromExtension(string ext)
        {
            ext = (ext ?? "").ToLowerInvariant();
            return ext switch
            {
                ".webm" => "audio/webm",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                _ => "application/octet-stream"
            };
        }
    }
}
