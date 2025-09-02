using System.Text;
using EPApi.Models;

namespace EPApi.Services
{
    public static class AiOpinionPromptBuilder
    {
        public static string Fingerprint(AttemptAiBundle b, string promptVersion)
        {
            var sb = new StringBuilder();
            sb.AppendLine(promptVersion);
            sb.AppendLine(b.TestName);
            foreach (var s in b.CurrentScales)
                sb.Append($"{s.Code}:{s.Raw}/{s.Min}-{s.Max}|");
            sb.AppendLine();
            sb.AppendLine("INIT:" + (b.InitialInterviewText ?? ""));
            foreach (var t in b.PreviousTests)
            {
                sb.Append("|" + t.TestName + ":");
                foreach (var s in t.Scales) sb.Append($"{s.Code}:{s.Raw}/{s.Min}-{s.Max},");
            }
            return sb.ToString();
        }

        public static string Build(AttemptAiBundle b, string promptVersion)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[PROMPT_VERSION: {promptVersion}]");
            sb.AppendLine("Eres un asistente clínico. Escribe una síntesis breve, prudente y NO diagnóstica.");
            sb.AppendLine("Evita jergas, incluye patrones relevantes, posibles sesgos cognitivos y una recomendación práctica general.");
            sb.AppendLine();

            sb.AppendLine($"Test actual: {b.TestName}");
            sb.AppendLine("Resultados actuales por escala (raw / min-max):");
            foreach (var s in b.CurrentScales)
                sb.AppendLine($"- {s.Name} ({s.Code}): {s.Raw} / {s.Min}-{s.Max}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(b.InitialInterviewText))
            {
                sb.AppendLine("Fragmentos de la entrevista inicial:");
                sb.AppendLine(b.InitialInterviewText);
                sb.AppendLine();
            }

            if (b.PreviousTests.Count > 0)
            {
                sb.AppendLine("Resumen de tests previos:");
                foreach (var t in b.PreviousTests)
                {
                    sb.AppendLine($"· {t.TestName}");
                    foreach (var s in t.Scales)
                        sb.AppendLine($"  - {s.Name} ({s.Code}): {s.Raw} / {s.Min}-{s.Max}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("Redacta en español, en un solo bloque de 8–12 líneas.");
            return sb.ToString();
        }
    }
}
