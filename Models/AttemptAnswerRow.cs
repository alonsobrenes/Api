// Models/AttemptAnswerRow.cs
namespace EPApi.Models
{
    public sealed class AttemptAnswerRow
    {
        public Guid QuestionId { get; set; }
        public string Code { get; set; } = "";
        public string Text { get; set; } = "";
        public string QuestionType { get; set; } = "";
        public string? AnswerText { get; set; }          // para preguntas abiertas
        public string? AnswerValue { get; set; }         // para single choice / likert
        public string? AnswerValuesJson { get; set; }    // para multi (JSON si aplica)
        public int OrderNo { get; set; }                 // para ordenar visualmente
    }

    public sealed class AttemptAnswerWriteDto
    {
        public Guid QuestionId { get; set; }
        public string? Text { get; set; }          // para open_text
        public string? Value { get; set; }         // para single
        public string? ValuesJson { get; set; }    // JSON para multi
    }
}
