namespace EPApi.Models
{
    public sealed class TestQuestionCreateDto
    {
        public string Code { get; set; } = default!;
        public string Text { get; set; } = default!;
        public string QuestionType { get; set; } = default!; // 'likert' | 'boolean' | 'open_ended'
        public int OrderNo { get; set; } = 0;
        public bool IsOptional { get; set; } = false;
    }

    public sealed class TestQuestionUpdateDto
    {
        public string Text { get; set; } = default!;
        public string QuestionType { get; set; } = default!;
        public int OrderNo { get; set; } = 0;
        public bool IsOptional { get; set; } = false;
        // Nota: Code no se edita (si lo necesitas, lo agregamos luego)
    }
}
