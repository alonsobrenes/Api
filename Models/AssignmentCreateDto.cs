namespace EPApi.Models
{
    public sealed class AssignmentCreateDto
    {
        public Guid TestId { get; set; }
        public Guid PatientId { get; set; }
        public string? RespondentRole { get; set; }   // 'self','parent',...
        public string? RelationLabel { get; set; }    // 'madre','padre',...
        public DateTime? DueAt { get; set; }
    }

    public sealed class AssignmentCreatedDto
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "pending";
    }
}
