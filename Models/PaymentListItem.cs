namespace EPApi.Models
{
    // Models/Billing/PaymentListItem.cs
    public sealed class PaymentListItem
    {
        public Guid Id { get; set; }
        public Guid OrgId { get; set; }
        public string Provider { get; set; } = "tilopay";               // e.g. "tilopay"
        public string? ProviderPaymentId { get; set; }                // tilopay_payment_id
        public string? OrderNumber { get; set; }
        public int AmountCents { get; set; }
        public string CurrencyIso { get; set; } = "USD";               // char(3)
        public string Status { get; set; } = default!;                 // e.g. "succeeded","failed","pending"
        public string? ErrorCode { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

}
