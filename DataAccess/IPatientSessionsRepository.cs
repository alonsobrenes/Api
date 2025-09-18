using EPApi.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.DataAccess
{
    // Expected external types:
    // - PatientSessionDto (record with CreatedByUserId included)
    // - PagedResult<T>      (Items + Total)
    public interface IPatientSessionsRepository
    {
        Task<PagedResult<PatientSessionDto>> ListAsync(
            Guid orgId, Guid patientId, int skip, int take, string? q, int? createdByUserId, CancellationToken ct);

        Task<PatientSessionDto> GetAsync(Guid orgId, Guid patientId, Guid id, CancellationToken ct);

        Task<PatientSessionDto> CreateAsync(
            Guid orgId, Guid patientId, int createdByUserId, string title, string? content, CancellationToken ct);

        Task<PatientSessionDto> UpdateAsync(
            Guid orgId, Guid patientId, Guid id, string title, string? content, CancellationToken ct);

        Task SoftDeleteAsync(Guid orgId, Guid patientId, Guid id, CancellationToken ct);

        Task<string?> GetRawContentAsync(Guid orgId, Guid patientId, Guid id, CancellationToken ct);

        Task<PatientSessionDto> UpdateAiTidyAsync(
            Guid orgId, Guid patientId, Guid id, string? aiTidy, CancellationToken ct);

        Task<PatientSessionDto> UpdateAiOpinionAsync(
            Guid orgId, Guid patientId, Guid id, string? aiOpinion, CancellationToken ct);

        Task UpsertExplicitHashtagsAsync(Guid orgId, Guid sessionId, string text, CancellationToken ct);

        Task<string> ExportPlainTextAsync(Guid orgId, Guid patientId, Guid id, CancellationToken ct);
    }
}
