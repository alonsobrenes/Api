namespace EPApi.Models
{
    public sealed class TestListItem
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = default!;
        public string Name { get; set; } = default!;
        public string? Description { get; set; }
        public string AgeGroupCode { get; set; } = default!;
        public string AgeGroupName { get; set; } = default!;
        public string? PdfUrl { get; set; }
        public bool IsActive { get; set; }
        public int QuestionCount { get; set; }
        public int ScaleCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public sealed class TestDetail
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = default!;
        public string Name { get; set; } = default!;
        public string? Description { get; set; }
        public string? Instructions { get; set; }
        public string? Example { get; set; }

        public Guid AgeGroupId { get; set; }            // <-- añadido para que el front pueda preseleccionar el dropdown
        public string AgeGroupCode { get; set; } = default!;
        public string AgeGroupName { get; set; } = default!;

        public string? PdfUrl { get; set; }
        public bool IsActive { get; set; }
        public int QuestionCount { get; set; }
        public int ScaleCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public sealed class TestQuestionRow
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = default!;
        public string Text { get; set; } = default!;
        public string QuestionType { get; set; } = default!;
        public int OrderNo { get; set; }
        public bool IsOptional { get; set; }
    }

    public sealed class TestScaleRow
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = default!;
        public string Name { get; set; } = default!;
        public string? Description { get; set; }
        public int QuestionCount { get; set; }
    }

    public sealed class TestsForMeFilters
    {
        public int? DisciplineId { get; set; }
        public string? DisciplineCode { get; set; }

        public int? CategoryId { get; set; }
        public string? CategoryCode { get; set; }

        public int? SubcategoryId { get; set; }
        public string? SubcategoryCode { get; set; }
    }

    public sealed class TaxonomyItemDto
    {
        public int DisciplineId { get; set; }
        public string DisciplineCode { get; set; } = "";
        public string DisciplineName { get; set; } = "";

        public int? CategoryId { get; set; }
        public string? CategoryCode { get; set; }
        public string? CategoryName { get; set; }

        public int? SubcategoryId { get; set; }
        public string? SubcategoryCode { get; set; }
        public string? SubcategoryName { get; set; }
    }

    public sealed class TestListItemDto
    {
        public Guid Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string? Instructions { get; set; }
        public string? PdfUrl { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string AgeGroupCode { get; set; } = "";
        public string AgeGroupName { get; set; } = "";
        public int QuestionCount { get; set; }
        public int ScaleCount { get; set; }

        // NUEVO: la UI lo necesita para filtrar localmente
        public List<TaxonomyItemDto> Taxonomy { get; set; } = new();
    }
}
