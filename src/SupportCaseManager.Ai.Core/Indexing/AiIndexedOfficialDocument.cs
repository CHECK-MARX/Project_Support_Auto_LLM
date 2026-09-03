using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record class AiIndexedOfficialDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("sectionTitle")]
    public string SectionTitle { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("retrievedAt")]
    public DateTimeOffset RetrievedAt { get; init; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = string.Empty;

    [JsonPropertyName("logicalSourceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogicalSourceId { get; init; }

    [JsonPropertyName("logicalSourceLocator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LogicalSourceLocator? LogicalSourceLocator { get; init; }

    [JsonPropertyName("parsedSourceAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ParsedSourceAddress? ParsedSourceAddress { get; init; }

    [JsonPropertyName("chunkId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChunkId { get; init; }

    [JsonPropertyName("chunkLocator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChunkLocator? ChunkLocator { get; init; }

    [JsonPropertyName("indexLookupKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IndexLookupKey { get; init; }
}
