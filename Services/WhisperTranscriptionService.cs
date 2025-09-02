// Services/WhisperTranscriptionService.cs
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Headers;
using static EPApi.Controllers.ClinicianInterviewsController;

namespace EPApi.Services
{
    public sealed class WhisperTranscriptionService : ITranscriptionService
    {
        private readonly IHttpClientFactory _http;
        private readonly string _apiKey;
        private readonly Random _rng = new();

        public WhisperTranscriptionService(IHttpClientFactory http, IConfiguration cfg)
        {
            _http = http;

            Console.WriteLine(cfg["OpenAI:ApiKey"]);
            Console.WriteLine(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));

            _apiKey = cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                      ?? throw new InvalidOperationException("Missing OpenAI API key");
        }

        public async Task<(string? language, string text, string? wordsJson)> TranscribeAsync(string absolutePath, CancellationToken ct = default)
        {
            var client = _http.CreateClient();
            client.BaseAddress = new Uri("https://api.openai.com/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            // 3 intentos con backoff exponencial + jitter
            var attempts = 0;
            var delayBase = TimeSpan.FromSeconds(2);

            while (true)
            {
                attempts++;
                using var form = new MultipartFormDataContent
                {
                    { new StringContent("whisper-1"), "model" },
                    // { new StringContent("es"), "language" }, // opcional
                };

                var fileName = Path.GetFileName(absolutePath);
                var mime = GetMimeFromExtension(Path.GetExtension(absolutePath));
                await using var fs = File.OpenRead(absolutePath);
                var fileContent = new StreamContent(fs);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(mime);
                form.Add(fileContent, "file", fileName);
                form.Add(new StringContent("es"), "language");

                using var resp = await client.PostAsync("v1/audio/transcriptions", form, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                
                if (resp.IsSuccessStatusCode)
                {
                    var json = System.Text.Json.JsonDocument.Parse(body).RootElement;
                    var text = json.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    return ("es", text, null);
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
