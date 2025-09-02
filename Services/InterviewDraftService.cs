using EPApi.DataAccess;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EPApi.Services
{
    public interface IInterviewDraftService
    {
        Task<string> GenerateDraftAsync(Guid interviewId, string promptVersion, CancellationToken ct = default);
    }

    public sealed class InterviewDraftService : IInterviewDraftService
    {
        private readonly IHttpClientFactory _http;
        private readonly IConfiguration _cfg;
        private readonly IInterviewsRepository _repo;

        public InterviewDraftService(IHttpClientFactory http, IConfiguration cfg, IInterviewsRepository repo)
        {
            _http = http;
            _cfg = cfg;
            _repo = repo;
        }

        public async Task<string> GenerateDraftAsync(Guid interviewId, string promptVersion, CancellationToken ct = default)
        {
            // 1) Trae transcripción y datos básicos del paciente
            var transcript = await _repo.GetLatestTranscriptTextAsync(interviewId, ct);
            if (string.IsNullOrWhiteSpace(transcript))
                throw new InvalidOperationException("No hay transcripción para esta entrevista.");

            var p = await _repo.GetInterviewPatientAsync(interviewId, ct);
            var patient = new
            {
                id = p?.PatientId,
                name = p?.FullName,
                sex = p?.Sex,
                birthDate = p?.BirthDate
            };

            // 2) Arma mensajes (prompt en español)
            var system = """
Eres un asistente clínico que redacta un BORRADOR NO OFICIAL para la historia clínica.
Idioma: español neutro. Estilo: claro, conciso y profesional. Tono: descriptivo, sin juicios.
NO emitas diagnósticos definitivos ni afirmaciones categóricas.
Incluye advertencia de “borrador no oficial generado con IA”.
Estructura en secciones y viñetas. Limita cada sección a lo esencial.
""";

            var user = new
            {
                promptVersion,
                patient,
                instructions = """
Redacta un borrador breve con el siguiente formato:

(Borrador NO OFICIAL)

Motivo de consulta
- …

Historia relevante
- …

Observaciones del entrevistador
- …

Impresiones clínicas (hipótesis)
- …

Transcripción resumida (extracto):
…

Recomendaciones
- …

Advertencias
- Este texto es un borrador no oficial generado con apoyo de IA.

Notas:
- No inventes datos ausentes; deja “(Completar)” cuando falte.
- Mantén nombres propios que vengan en la transcripción, pero no añadas otros.
- Si hay riesgo, menciónalo como hipótesis, no como hecho.
""",
                transcript
            };

            // 3) Llama a OpenAI (chat completions)
            var apiKey = _cfg["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                         ?? throw new InvalidOperationException("Falta OpenAI:ApiKey");

            Console.WriteLine(apiKey);

            Console.WriteLine("asdkjaslkdja");

            var client = _http.CreateClient();
            client.BaseAddress = new Uri("https://api.openai.com/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var payload = new
            {
                model = _cfg["OpenAI:Model"] ?? "gpt-4o-mini",
                temperature = 0.2,
                max_tokens = 1200,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = JsonSerializer.Serialize(user) }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"OpenAI error {(int)resp.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement
                             .GetProperty("choices")[0]
                             .GetProperty("message")
                             .GetProperty("content")
                             .GetString() ?? "";

            return content.Trim();
        }
    }
}
