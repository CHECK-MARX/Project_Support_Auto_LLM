using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class QuestionClassificationResult
{
    [JsonPropertyName("questionTypes")]
    public IReadOnlyList<string> QuestionTypes { get; init; } = [];

    [JsonPropertyName("currentInstalledVersion")]
    public string CurrentInstalledVersion { get; init; } = string.Empty;

    [JsonPropertyName("requestedFacts")]
    public IReadOnlyList<string> RequestedFacts { get; init; } = [];

    [JsonPropertyName("explanation")]
    public string Explanation { get; init; } = string.Empty;
}
