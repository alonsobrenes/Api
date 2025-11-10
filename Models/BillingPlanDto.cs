namespace EPApi.Models
{
    public class BillingPlanDto
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = default!;
        public string Name { get; set; } = default!;
        public string? Description { get; set; }
        public string Period { get; set; } = "monthly";  // monthly, yearly, etc.
        public bool IsActive { get; set; }
        public bool IsPublic { get; set; }
        public int TrialDays { get; set; }
        public string Currency { get; set; } = "USD";
        public int PriceAmountCents { get; set; }
        public string? Provider { get; set; }
        public string? ProviderPriceId { get; set; }
    }
}
