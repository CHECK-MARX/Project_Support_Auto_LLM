using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class ResolvedFact
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "Missing";

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "Low";

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("sourceUrls")]
    public IReadOnlyList<string> SourceUrls { get; init; } = [];

    [JsonPropertyName("explanation")]
    public string Explanation { get; init; } = string.Empty;
}
