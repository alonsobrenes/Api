using System;

namespace EPApi.Models
{
    public sealed class CreateInterviewDto
    {
        public Guid PatientId { get; set; }
    }

    public sealed class SaveTranscriptDto
    {
        public string? Language { get; set; } = "es";
        public string Text { get; set; } = "";
        public string? WordsJson { get; set; }
    }

    public sealed class DiagnosisReq
    {
        public string? Notes { get; set; } // contexto del clínico
        public string? PromptVersion { get; set; } = "v1";
        public string? Model { get; set; } // auditoría
    }

    public sealed class SaveDraftDto
    {
        public string Content { get; set; } = "";
    }

    public sealed class InterviewSummaryDto
    {
        public Guid Id { get; set; }
        public Guid PatientId { get; set; }
        public int AudioCount { get; set; }
        public string? LatestTranscript { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime? EndedAtUtc { get; set; }
        public string Status { get; set; } = "created";
    }
}
