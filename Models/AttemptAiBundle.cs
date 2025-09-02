namespace EPApi.Models
{
    public sealed record AttemptAiBundle(
        Guid AttemptId,
        Guid PatientId,
        string TestName,
        IReadOnlyList<ScaleRow> CurrentScales,
        string? InitialInterviewText,
        IReadOnlyList<TestSummary> PreviousTests
    );

    public sealed record ScaleRow(string Code, string Name, decimal Raw, decimal Min, decimal Max);
    public sealed record TestSummary(string TestName, DateTime FinishedAtUtc, IReadOnlyList<ScaleRow> Scales);
}
