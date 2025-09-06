using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace EPApi.Services
{
    public sealed class OpenAiAssistantService : IAiAssistantService
    {
        private readonly IHttpClientFactory _http;
        private readonly IConfiguration _cfg;

        public OpenAiAssistantService(IHttpClientFactory http, IConfiguration cfg)
        {
            _http = http;
            _cfg = cfg;
        }

        private string? Resolve(string key)
            => (_cfg[key] ??
                _cfg[key.Replace(":", "__")] ??
                Environment.GetEnvironmentVariable(key.Replace(":", "__").ToUpperInvariant()))
               ?.Trim();

        private (string apiKey, string model, string? project, string? org) GetAuth()
        {
            var apiKey =
                Resolve("OpenAI:ApiKey") ??
                Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("AI not configured: missing OpenAI API key.");

            var model = Resolve("OpenAI:Model") ?? "gpt-4o-mini";
            var project = Resolve("OpenAI:Project");
            var org = Resolve("OpenAI:Organization");

            return (apiKey, model, project, org);
        }

        public async Task<AiOpinionResult> GenerateOpinionAsync(string prompt, string model, CancellationToken ct)
        {
            // === Auth & config ===
            (var apiKey, var defaultModel, var project, var org) = GetAuth();
            var useModel = string.IsNullOrWhiteSpace(model) ? defaultModel : model;

            var cli = _http.CreateClient();

            // Headers básicos
            cli.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (!string.IsNullOrWhiteSpace(project))
                cli.DefaultRequestHeaders.Add("OpenAI-Project", project);
            if (!string.IsNullOrWhiteSpace(org))
                cli.DefaultRequestHeaders.Add("OpenAI-Organization", org);

            // ======= 1) Chat Completions =======
            var body = new
            {
                model = useModel,
                messages = new object[]
                {
                    new { role = "system", content = "Eres un asistente clínico que redacta síntesis claras y prudentes." },
                    new { role = "user",   content = prompt }
                },
                temperature = 0.2
            };

            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };

            var res = await cli.SendAsync(req, ct);

            // Si falla, lee mensaje de error para depurar
            if (!res.IsSuccessStatusCode)
            {
                var errText = await res.Content.ReadAsStringAsync(ct);

                // 400/404 por modelo/endpoint -> probar Responses API
                if (res.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                    res.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var r2 = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            model = useModel,
                            input = prompt,
                            temperature = 0.2
                        }), Encoding.UTF8, "application/json")
                    };

                    var res2 = await cli.SendAsync(r2, ct);
                    if (!res2.IsSuccessStatusCode)
                    {
                        var e2 = await res2.Content.ReadAsStringAsync(ct);
                        throw new HttpRequestException($"OpenAI responses error {(int)res2.StatusCode}: {e2}");
                    }

                    using var s2 = await res2.Content.ReadAsStreamAsync(ct);
                    using var d2 = await JsonDocument.ParseAsync(s2, cancellationToken: ct);
                    // Responses API: 'output_text' conveniente
                    var text2 = d2.RootElement.TryGetProperty("output_text", out var ot)
                        ? ot.GetString() ?? ""
                        : d2.RootElement.ToString();
                    return new AiOpinionResult(text2, null, null);
                }

                // 401/403: credencial/ámbito/headers
                if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    res.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new HttpRequestException($"OpenAI auth error {(int)res.StatusCode}. Revisa API key/proyecto/organización. Body: {errText}");
                }

                // Otros
                throw new HttpRequestException($"OpenAI error {(int)res.StatusCode}: {errText}");
            }

            // OK: parse chat completions
            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return new AiOpinionResult(content, null, null);
        }
    }
}
