namespace EPApi.Models
{
    public sealed class TestTaxonomyRow
    {
        public int DisciplineId { get; set; }
        public string DisciplineCode { get; set; } = default!;
        public string DisciplineName { get; set; } = default!;
        public int? CategoryId { get; set; }
        public string? CategoryCode { get; set; }
        public string? CategoryName { get; set; }
        public int? SubcategoryId { get; set; }
        public string? SubcategoryCode { get; set; }
        public string? SubcategoryName { get; set; }
    }

    public sealed class TestTaxonomyWriteItem
    {
        public int DisciplineId { get; set; }
        public int? CategoryId { get; set; }
        public int? SubcategoryId { get; set; }
    }

    public sealed class TestTaxonomyWriteDto
    {
        public TestTaxonomyWriteItem[] Items { get; set; } = Array.Empty<TestTaxonomyWriteItem>();
    }
}
