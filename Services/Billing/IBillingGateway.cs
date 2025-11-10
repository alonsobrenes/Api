// Services/Billing/IBillingGateway.cs
using EPApi.Models;
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

    public sealed class PaymentMethodDetails
    {
        public string? Brand { get; init; }
        public string? Last4 { get; init; }
        public int? ExpMonth { get; init; }
        public int? ExpYear { get; init; }
        public string? RawProviderPayload { get; init; }
    }

    public interface IBillingGateway
    {
        Task<string> CreateCheckoutSession(Guid orgId, string planCode, string returnUrl, CancellationToken ct);
        Task<string> GetCustomerPortalUrl(Guid orgId, CancellationToken ct);
        Task<IReadOnlyList<BillingEvent>> ParseWebhook(HttpRequest request, CancellationToken ct);
        Task<string> CreateTokenizationSessionAsync(Guid orgId, string returnUrl, CancellationToken ct);
        Task<PaymentMethodDetails?> TryFetchPaymentMethodDetailsAsync(string providerPmId, CancellationToken ct);
        Task<string> CreateRepeatPlanAsync(BillingPlanDto plan, CancellationToken ct);
        Task<(string? registerUrl, string? renewUrl, string rawJson)>
        GetRecurrentUrlAsync(string providerPlanId, string email, CancellationToken ct);
    }
}
