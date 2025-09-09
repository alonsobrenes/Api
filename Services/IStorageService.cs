using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Services.Storage
{
    public interface IStorageService
    {
        Task<(Guid fileId, long bytes)> SaveAsync(
            Guid orgId, Guid patientId,
            Stream content, string contentType, string originalName,
            string? comment, int? uploadedByUserId,
            CancellationToken ct);

        Task<(Stream content, string contentType, string downloadName)> OpenReadAsync(
            Guid fileId, CancellationToken ct);

        Task<IReadOnlyList<PatientFileDto>> ListAsync(
            Guid orgId, Guid patientId, CancellationToken ct);

        Task<bool> SoftDeleteAsync(Guid fileId, int? deletedByUserId, CancellationToken ct);
    }

    public sealed record PatientFileDto(
        Guid fileId, string name, string contentType, long size,
        DateTime uploadedAtUtc, string? comment, bool deleted);
}
