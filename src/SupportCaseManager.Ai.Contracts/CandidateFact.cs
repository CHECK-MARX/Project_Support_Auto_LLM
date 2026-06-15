using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class CandidateFact
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "Low";

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("sourceUrl")]
    public string SourceUrl { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("extractedAt")]
    public DateTimeOffset ExtractedAt { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}
