using System;
using System.Threading;
using System.Threading.Tasks;
using static EPApi.Controllers.ClinicianInterviewsController;

namespace EPApi.Services
{
    public interface IInterviewsRepository
    {
        // Audio
        Task AddAudioAsync(Guid interviewId, string uri, string mimeType, long? durationMs, CancellationToken ct = default);
        Task<(string Uri, string Mime)?> GetLatestAudioAsync(Guid interviewId, CancellationToken ct = default);

        // Estado
        Task SetStatusAsync(Guid interviewId, string status, CancellationToken ct = default);

        // Transcripción
        Task SaveTranscriptAsync(Guid interviewId, string? language, string text, string? wordsJson, CancellationToken ct = default);
        Task<string?> GetLatestTranscriptTextAsync(Guid interviewId, CancellationToken ct = default);

        // Paciente de la entrevista (para IA)
        Task<(Guid PatientId, string? FullName, string? Sex, DateTime? BirthDate)?> GetInterviewPatientAsync(Guid interviewId, CancellationToken ct = default);

        Task SaveDraftAsync(Guid interviewId, string content, int assignedByUserId, string? model, string? promptVersion, CancellationToken ct = default);

        Task UpdateClinicianDiagnosisAsync(Guid interviewId, string? text, bool close, CancellationToken ct = default);


        Task<Guid> CreateInterviewAsync(Guid patientId, CancellationToken ct = default);

        Task<FirstInterviewDto?> GetFirstInterviewByPatientAsync(Guid patientId, CancellationToken ct = default);

    }
}
