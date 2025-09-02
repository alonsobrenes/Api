namespace EPApi.Models
{
    public sealed class TestQuestionOptionRow
    {
        public Guid Id { get; set; }
        public Guid QuestionId { get; set; }
        public int Value { get; set; }      // 0,1,2...
        public string Label { get; set; } = default!;
        public int OrderNo { get; set; }
        public bool IsActive { get; set; }
    }
}
