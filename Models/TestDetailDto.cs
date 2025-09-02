// Models/TestDetailDto.cs
namespace EPApi.Models
{
    public sealed class TestDetailDto
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = default!;
        public string Name { get; set; } = default!;
        public string? Description { get; set; }
        public string? Instructions { get; set; }
        public string? Example { get; set; }
        public string? PdfUrl { get; set; }

        public Guid AgeGroupId { get; set; }              // <-- IMPORTANTE
        public string AgeGroupCode { get; set; } = "";    // ya lo tenías
        public string AgeGroupName { get; set; } = "";    // ya lo tenías

        public bool IsActive { get; set; }
        public int QuestionCount { get; set; }
        public int ScaleCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
