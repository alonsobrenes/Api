// Models/TestRunSubmitDto.cs
namespace EPApi.Models
{
    public sealed class TestRunSubmitDto
    {
        public Guid TestId { get; set; }
        public Guid? PatientId { get; set; }
        public Guid? AssignmentId { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime FinishedAtUtc { get; set; }
        public List<TestRunAnswerDto>? Answers { get; set; } = new();
    }

    public sealed class TestRunAnswerDto
    {
        public Guid QuestionId { get; set; }
        public string? Value { get; set; }            // para single (guardamos como string; numérico si aplica)
        public List<string>? Values { get; set; }     // para multi
        public string? Text { get; set; }             // open_text
    }

    public sealed class TestRunSubmitResultDto
    {
        public Guid RunId { get; set; }
        public Guid TestId { get; set; }
        public Guid? PatientId { get; set; }
        public DateTime FinishedAtUtc { get; set; }
        public List<ScaleScoreDto>? Scales { get; set; } = new();
        public double? TotalRaw { get; set; }
        public double? TotalMax { get; set; }
        public double? TotalMin { get; set; }
        public double? TotalPercent { get; set; }
    }

    public sealed class ScaleScoreDto
    {
        public Guid ScaleId { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public double Raw { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double? Percent { get; set; }
    }
}
