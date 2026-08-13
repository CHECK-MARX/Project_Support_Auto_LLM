using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record class EmbeddingIndexDocument
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "embeddings-index.json";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("embeddingModel")]
    public string EmbeddingModel { get; init; } = string.Empty;

    [JsonPropertyName("builtAt")]
    public DateTimeOffset BuiltAt { get; init; }

    [JsonPropertyName("entries")]
    public IReadOnlyList<EmbeddingIndexEntry> Entries { get; init; } = [];
}

public sealed record class EmbeddingIndexEntry
{
    [JsonPropertyName("sourceId")]
    public string SourceId { get; init; } = string.Empty;

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = string.Empty;

    [JsonPropertyName("vector")]
    public IReadOnlyList<float> Vector { get; init; } = [];
}

public sealed record class EmbeddingIndexUpdateResult
{
    public bool IsSuccess { get; init; }

    public string EmbeddingModel { get; init; } = string.Empty;

    public string IndexFilePath { get; init; } = string.Empty;

    public int AddedCount { get; init; }

    public int ChangedCount { get; init; }

    public int DeletedCount { get; init; }

    public int UnchangedCount { get; init; }

    public int VectorCount { get; init; }

    public string Warning { get; init; } = string.Empty;
}
