using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class EvidenceItem
{
    [JsonPropertyName("sourceId")]
    public string SourceId { get; init; } = string.Empty;

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("excerpt")]
    public string Excerpt { get; init; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string? FilePath { get; init; }

    [JsonPropertyName("supportNumber")]
    public string? SupportNumber { get; init; }

    [JsonPropertyName("relevance")]
    public double Relevance { get; init; }
}
