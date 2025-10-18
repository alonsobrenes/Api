namespace EPApi.Services
{
    public sealed record AiOpinionResult(string Text, string? Json, byte? RiskLevel, int? PromptTokens, int? CompletionTokens, int? TotalTokens);

    public interface IAiAssistantService
    {
        Task<AiOpinionResult> GenerateOpinionAsync(string prompt, string model, CancellationToken ct);
    }
}
