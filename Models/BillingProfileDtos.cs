namespace EPApi.Models
{
    public sealed class AddressDto
    {
        public string Line1 { get; set; } = default!;
        public string? Line2 { get; set; }
        public string City { get; set; } = default!;
        public string? StateRegion { get; set; }
        public string PostalCode { get; set; } = default!;
        public string CountryIso2 { get; set; } = default!;
    }

    public sealed class BillingProfileDto
    {
        public string LegalName { get; set; } = default!;
        public string? TradeName { get; set; }
        public string TaxId { get; set; } = default!;

        public string ContactEmail { get; set; } = default!;
        public string? ContactPhone { get; set; }
        public string? Website { get; set; }

        public AddressDto BillingAddress { get; set; } = new AddressDto();
        public AddressDto? ShippingAddress { get; set; }
       
    }
}
