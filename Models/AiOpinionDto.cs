using System.Text.Json.Serialization;

public sealed class AiOpinionDto
{
    [JsonPropertyName("opinionText")]
    public string? OpinionText { get; set; }

    [JsonPropertyName("opinionJson")]
    public string? OpinionJson { get; set; }

    [JsonPropertyName("riskLevel")]
    public byte? RiskLevel { get; set; }

    [JsonPropertyName("modelVersion")]
    public string? ModelVersion { get; set; }

    [JsonPropertyName("promptVersion")]
    public string? PromptVersion { get; set; }

    [JsonPropertyName("inputHash")]
    public string? InputHash { get; set; }
}
