// Models/PatientAssessmentRow.cs
namespace EPApi.Models
{
    public sealed class PatientAssessmentRow
    {
        public Guid AttemptId { get; set; }
        public Guid PatientId { get; set; }
        public Guid TestId { get; set; }
        public string TestCode { get; set; } = "";
        public string TestName { get; set; } = "";
        public string? ScoringMode { get; set; }
        public string Status { get; set; } = "";
        public DateTime? StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool ReviewFinalized { get; set; }
    }
}
