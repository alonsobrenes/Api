namespace EPApi.Models
{
    public sealed class TestTopItem
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public int UsageCount { get; set; }
    }
}
