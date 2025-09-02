namespace EPApi.Models
{
    public sealed class TestDisciplineItem
    {
        public int Id { get; set; }
        public string Code { get; set; } = default!;
        public string Name { get; set; } = default!;
    }

    public sealed class TestDisciplinesReadDto
    {
        public List<TestDisciplineItem> Disciplines { get; set; } = new();
    }

    // Para PUT: solo ids
    public sealed class TestDisciplinesWriteDto
    {
        public int[] DisciplineIds { get; set; } = Array.Empty<int>();
    }
}
