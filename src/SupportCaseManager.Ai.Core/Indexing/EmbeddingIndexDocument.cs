using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record class EmbeddingIndexDocument
{
    public const int CurrentSchemaVersion = 2;
    public const string FileName = "embeddings-index.json";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("embeddingModel")]
    public string EmbeddingModel { get; init; } = string.Empty;

    [JsonPropertyName("embeddingProvider")]
    public string EmbeddingProvider { get; init; } = "Ollama";

    [JsonPropertyName("embeddingModelIdentifier")]
    public string EmbeddingModelIdentifier { get; init; } = string.Empty;

    // Optional metadata keeps existing v2 index documents readable.
    [JsonPropertyName("embeddingModelDigest")]
    public string EmbeddingModelDigest { get; init; } = string.Empty;

    [JsonPropertyName("embeddingDimension")]
    public int EmbeddingDimension { get; init; }

    [JsonPropertyName("embeddingNormalized")]
    public bool EmbeddingNormalized { get; init; }

    [JsonPropertyName("distanceMetric")]
    public string DistanceMetric { get; init; } = "cosine";

    [JsonPropertyName("builtAt")]
    public DateTimeOffset BuiltAt { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

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

    [JsonPropertyName("chunkContentHash")]
    public string ChunkContentHash { get; init; } = string.Empty;

    [JsonPropertyName("embeddingInputContentHash")]
    public string EmbeddingInputContentHash { get; init; } = string.Empty;

    [JsonPropertyName("documentLocator")]
    public string DocumentLocator { get; init; } = string.Empty;

    [JsonPropertyName("embeddingInputSanitized")]
    public bool EmbeddingInputSanitized { get; init; }

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

    public string Status { get; init; } = "NotStarted";

    public int EmbeddingDimension { get; init; }
}

public sealed record class EmbeddingIndexValidationResult
{
    public bool IsValid { get; init; }

    public int VectorCount { get; init; }

    public int InvalidVectorCount { get; init; }

    public int DuplicateVectorAnomalyCount { get; init; }

    public string Message { get; init; } = string.Empty;
}
