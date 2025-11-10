namespace EPApi.Services.Billing
{
    public sealed class PaymentMethodTokenizeContext
    {
        public string Email { get; init; } = default!;
        public string FirstName { get; init; } = default!;
        public string LastName { get; init; } = default!;
        public string Language { get; init; } = "es"; // "es" | "en"
    }

    public interface IPaymentMethodTokenizeContextProvider
    {
        Task<PaymentMethodTokenizeContext> GetContextAsync(Guid orgId, CancellationToken ct);
    }
}
