// DataAccess/IClinicianReviewRepository.cs
using EPApi.Models;
using Microsoft.AspNetCore.Mvc;

namespace EPApi.DataAccess
{
    public interface IClinicianReviewRepository
    {
        Task<IReadOnlyList<ScaleWithItemsDto>> GetScalesWithItemsAsync(Guid testId, CancellationToken ct = default);

        Task<CreateAttemptResultDto> CreateAttemptAsync(Guid testId, Guid? patientId, int assignedByUserId, CancellationToken ct = default);

        Task<AttemptReviewDto?> GetReviewAsync(Guid attemptId, CancellationToken ct = default);

        /// <summary>Guarda la revisión (borrador o final) y actualiza estado del intento.</summary>
        Task<Guid> UpsertReviewAsync(Guid attemptId, ReviewUpsertInputDto dto, CancellationToken ct = default);

        //Task<IReadOnlyList<PatientAssessmentRow>> ListAssessmentsByPatientAsync(
        //Guid patientId,
        //int? ownerUserId,
        //bool isAdmin,
        //CancellationToken ct);

        Task<IEnumerable<PatientAssessmentRow>> ListAssessmentsByPatientAsync(
     Guid patientId,
    int? viewerUserId,      // el que mira (doctor u owner)
    bool isOwner,           // dueño de la org
    Guid? orgId,            // org actual, obligatorio si isOwner = true
    CancellationToken ct = default);


        Task<bool> DeleteAttemptIfDraftAsync(Guid attemptId, int? ownerUserId, bool isAdmin, CancellationToken ct = default);
        Task<IReadOnlyList<AttemptAnswerRow>> GetAttemptAnswersAsync(
                Guid attemptId, int? ownerUserId, bool isAdmin, CancellationToken ct = default);
        Task UpsertAttemptAnswersAsync(Guid attemptId, IReadOnlyList<AttemptAnswerWriteDto> answers, CancellationToken ct = default);
        Task<Guid> LogAutoAttemptAsync(Guid testId, Guid? patientId, DateTime? startedAtUtc, int assignedByUserId, CancellationToken ct = default);
        Task<AttemptMetaDto?> GetAttemptMetaAsync(Guid attemptId, CancellationToken ct = default);
        Task FinalizeAttemptAsync(Guid attemptId, CancellationToken ct = default);
        Task<AttemptSummaryDto> GetAttemptSummaryAsync(
    DateTime fromUtc, DateTime toUtc, int? clinicianUserId, bool isAdmin, CancellationToken ct = default);

        
        Task<IReadOnlyList<PatientListItem>> ListRecentPatientsAsync(
    int? ownerUserId, bool isOwner, int take, CancellationToken ct = default);
        Task<IReadOnlyList<TestTopItem>> ListTopTestsAsync(
    DateTime fromUtc, DateTime toUtc, int? ownerUserId, bool isAdmin, int take, CancellationToken ct = default);
        Task<TestBasicDto?> GetBasicForClinicianByIdAsync(Guid id, CancellationToken ct = default);
        Task<TestBasicDto?> GetTestForClinicianByIdAsync(Guid id, CancellationToken ct = default);
        
        Task UpsertAiOpinionAsync(Guid attemptId, Guid patientId,
          string? text, string? json, string? model, string? promptVersion, string? inputHash, byte? risk, int? PromptTokens, int? CompletionTokens, int? TotalTokens,
          CancellationToken ct = default);

        Task<AiOpinionDto?> GetAiOpinionByAttemptAsync(Guid attemptId, CancellationToken ct = default);

        Task<AttemptAiBundle?> GetAttemptBundleForAiAsync(Guid attemptId, CancellationToken ct);
        Task<IReadOnlyList<TestBasicDto>> GetTestsForClinicianAsync(int userId, CancellationToken ct = default);
        Task<PatientsByPeriodStatsDto> GetPatientsByPeriodStatsAsync(DateTime fromUtc, DateTime toUtc, int? clinicianUserId, bool isAdmin, CancellationToken ct = default);
        Task<IReadOnlyList<ClinicianOrgPatientContactStatsDto>> GetOrgPatientsByProfessionalAsync(
    Guid orgId,
    DateTime fromUtc,
    DateTime toUtc,
    CancellationToken ct = default);

        Task<OrgPatientsByProfessionalStatsResponseDto> GetOrgPatientsByProfessionalStatsAsync(
    Guid orgId,
    DateTime fromUtc,
    DateTime toUtc,
    CancellationToken ct = default);
    }
}
