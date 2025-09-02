// Models/TestBasicDto.cs
namespace EPApi.Models
{
    public sealed class TestBasicDto
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? PdfUrl { get; set; }
        public bool IsActive { get; set; }
    }
}
