using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static EPApi.Controllers.MeSupportController;
using static EPApi.DataAccess.SupportRepository;

namespace EPApi.DataAccess
{
    public interface ISupportRepository
    {
        Task<Guid> CreateTicketAsync(int userId, Guid? orgId, string subject, string description, string? category, string? priority, CancellationToken ct = default);

        Task<IReadOnlyList<MyTicketRow>> GetMyTicketsAsync(int userId, int top = 50, string? status = null, string? q = null, CancellationToken ct = default);

        Task<TicketWithMessages?> GetTicketWithMessagesAsync(Guid id, int userId, CancellationToken ct = default);

        Task AddMessageAsync(Guid ticketId, int senderUserId, string body, CancellationToken ct = default);

        Task<IReadOnlyList<AdminTicketRow>> AdminListTicketsAsync(
                                        int top = 100,
                                        string? status = null,
                                        int? assignedToUserId = null,
                                        int? userId = null,
                                        string? category = null,
                                        string? priority = null,
                                        DateTime? createdFromUtc = null,
                                        DateTime? createdToUtc = null,
                                        string? q = null,
                                        CancellationToken ct = default);

        Task<TicketWithMessages?> AdminGetTicketWithMessagesAsync(Guid id, CancellationToken ct = default);

        Task AddAdminMessageAsync(Guid ticketId, int adminUserId, string body, bool isInternal, CancellationToken ct = default);

        Task UpdateTicketAsync(Guid id, string? status, int? assignedToUserId, CancellationToken ct = default);

        Task<int?> GetTicketOwnerUserIdAsync(Guid ticketId, CancellationToken ct = default);
        
        Task<bool> UpdateTicketStatusByOwnerAsync(Guid ticketId, int ownerUserId, string status, CancellationToken ct = default);

        Task<int> CountTicketsCreatedSinceAsync(int userId, DateTime sinceUtc, CancellationToken ct = default);

        Task<IReadOnlyList<OrgTicketRow>> GetOrgTicketsForOrgAsync(Guid orgId, int top = 100, CancellationToken ct = default);

        Task<TicketWithMessages?> GetTicketWithMessagesForOrgAsync(Guid id, Guid orgId, CancellationToken ct = default);

        public sealed class AdminTicketRow
        {
            public Guid Id { get; init; }
            public int UserId { get; init; }
            public Guid? OrgId { get; init; }
            public string Subject { get; init; } = "";
            public string Status { get; init; } = "open";
            public string? Priority { get; init; }
            public string? Category { get; init; }
            public int? AssignedToUserId { get; init; }
            public DateTime CreatedAtUtc { get; init; }
            public DateTime? UpdatedAtUtc { get; init; }
            public DateTime? LastMessageAtUtc { get; init; }
            public TicketOpenedBy? OpenedBy { get; init; }
        }

    }

    public sealed class MyTicketRow
    {
        public Guid Id { get; init; }
        public string Subject { get; init; } = "";
        public string Status { get; init; } = "open";
        public string? Priority { get; init; }
        public string? Category { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime? UpdatedAtUtc { get; init; }
        public DateTime? LastMessageAtUtc { get; init; }
    }

    public sealed class OrgTicketRow
    {
        public Guid Id { get; init; }
        public string Subject { get; init; } = "";
        public string Status { get; init; } = "open";
        public string? Priority { get; init; }
        public string? Category { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime? UpdatedAtUtc { get; init; }
        public DateTime? LastMessageAtUtc { get; init; }
        public int CreatedByUserId { get; init; }
        public string CreatedByName { get; init; } = "";
        public string CreatedByEmail { get; init; } = "";
    }

}
