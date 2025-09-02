namespace EPApi.Models
{
    public sealed class TestCrudDto
    {
        public sealed class TestCreateDto
        {
            public string Code { get; set; } = default!;
            public string Name { get; set; } = default!;
            public string? Description { get; set; }
            public string? Instructions { get; set; }
            public string? Example { get; set; }
            public Guid AgeGroupId { get; set; }     // dropdown
            public string? PdfUrl { get; set; }      // por ahora URL, upload vendrá luego
            public bool IsActive { get; set; } = true;
        }

        public sealed class TestUpdateDto
        {
            // Code NO se edita (si quieres, lo agregamos después)
            public string Name { get; set; } = default!;
            public string? Description { get; set; }
            public string? Instructions { get; set; }
            public string? Example { get; set; }
            public Guid AgeGroupId { get; set; }
            public string? PdfUrl { get; set; }
            public bool IsActive { get; set; } = true;
        }
    }
}
