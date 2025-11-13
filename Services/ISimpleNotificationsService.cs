using System;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Services
{
    public interface ISimpleNotificationsService
    {
        Task<Guid> CreateForUserAsync(
            int userId,
            string title,
            string body,          // <- usa 'body' (no 'message') según tu tabla
            string kind,          // info | success | warning | danger
            string? actionUrl,
            string? actionLabel,
            int? createdByUserId = null,
            CancellationToken ct = default);

        // ISimpleNotificationsService.cs
        Task<Guid> CreateForOrgAsync(
            int orgId,
            string title,
            string body,
            string kind,              // info|success|warning|danger
            string? actionUrl,
            string? actionLabel,
            int? createdByUserId = null,
            CancellationToken ct = default);

    }
}
