// Services/Billing/IBillingGateway.cs
using Microsoft.AspNetCore.Http;

namespace EPApi.Services.Billing
{
    public enum BillingEventKind
    {
        SubscriptionActivated,
        SubscriptionUpdated,
        SubscriptionCanceled
    }

    public sealed class BillingEvent
    {
        public BillingEventKind Kind { get; init; }
        public Guid OrgId { get; init; }
        public string PlanCode { get; init; } = "";
        public string Status { get; init; } = "active"; // e.g., active, canceled, trialing
        public DateTime? PeriodStartUtc { get; init; }
        public DateTime? PeriodEndUtc { get; init; }
        public string? RawProviderPayload { get; init; }
        public string Provider { get; init; } = "Fake";   // Gateway/provider origin
        public string? IdempotencyKey { get; init; }      // Event id or deterministic key
    }

    public interface IBillingGateway
    {
        Task<string> CreateCheckoutSession(Guid orgId, string planCode, string returnUrl, CancellationToken ct);
        Task<string> GetCustomerPortalUrl(Guid orgId, CancellationToken ct);
        Task<IReadOnlyList<BillingEvent>> ParseWebhook(HttpRequest request, CancellationToken ct);
    }
}
