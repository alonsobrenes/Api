namespace EPApi.Models
{
    public record PatientSessionDto(
    Guid Id,
    Guid PatientId,
    int CreatedByUserId,         
    string Title,
    string? ContentText,
    string? AiTidyText,
    string? AiOpinionText,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);


    public record CreatePatientSessionRequest(string Title, string? ContentText);
    public record UpdatePatientSessionRequest(string Title, string? ContentText);

    public record UpsertAiTextRequest(string? SourceText); // opcional; si null usa ContentText
    public record LabelsChangeRequest(int[] AssignIds, int[] UnassignIds);

    public sealed class PagedResult<T>
    {
        public required IReadOnlyList<T> Items { get; init; }
        public required int Total { get; init; }
    }
}
