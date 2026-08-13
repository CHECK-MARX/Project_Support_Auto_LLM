using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

public static class KnowledgeStatuses
{
    public const string Ready = "Ready";
    public const string UpdateAvailable = "UpdateAvailable";
    public const string Updating = "Updating";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string NotCreated = "NotCreated";
}

[Flags]
public enum KnowledgeUpdateScope
{
    None = 0,
    PastCases = 1,
    Manuals = 2,
    OfficialDocs = 4,
    All = PastCases | Manuals | OfficialDocs,
}

public sealed record class KnowledgeManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentChunkingVersion = "2026-07-v1";
    public const string FileName = "knowledge-manifest.json";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("sourcePathOrUrl")]
    public string SourcePathOrUrl { get; init; } = string.Empty;

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = "ProductKnowledge";

    [JsonPropertyName("lastModified")]
    public DateTimeOffset? LastModified { get; init; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = string.Empty;

    [JsonPropertyName("indexedAt")]
    public DateTimeOffset? IndexedAt { get; init; }

    [JsonPropertyName("chunkingVersion")]
    public string ChunkingVersion { get; init; } = CurrentChunkingVersion;

    [JsonPropertyName("embeddingModel")]
    public string EmbeddingModel { get; init; } = string.Empty;

    [JsonPropertyName("documentCount")]
    public int DocumentCount { get; init; }

    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; init; }

    [JsonPropertyName("lastSuccessfulUpdate")]
    public DateTimeOffset? LastSuccessfulUpdate { get; init; }

    [JsonPropertyName("lastUpdateResult")]
    public string LastUpdateResult { get; init; } = string.Empty;

    [JsonPropertyName("sources")]
    public IReadOnlyList<KnowledgeManifestSource> Sources { get; init; } = [];
}

public sealed record class KnowledgeManifestSource
{
    [JsonPropertyName("sourcePathOrUrl")]
    public string SourcePathOrUrl { get; init; } = string.Empty;

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("lastModified")]
    public DateTimeOffset? LastModified { get; init; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = string.Empty;

    [JsonPropertyName("indexedAt")]
    public DateTimeOffset? IndexedAt { get; init; }

    [JsonPropertyName("documentCount")]
    public int DocumentCount { get; init; }

    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; init; }
}

public sealed record class KnowledgeIndexStatus
{
    public string Status { get; init; } = KnowledgeStatuses.NotCreated;

    public string ProductName { get; init; } = string.Empty;

    public DateTimeOffset? LastUpdatedAt { get; init; }

    public int ManualDocumentCount { get; init; }

    public int ManualChunkCount { get; init; }

    public int OfficialDocumentCount { get; init; }

    public int OfficialChunkCount { get; init; }

    public int PastCaseCount { get; init; }

    public int PastCaseChunkCount { get; init; }

    public bool UsedExistingIndex { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed record class KnowledgeUpdateResult
{
    public KnowledgeIndexStatus Status { get; init; } = new();

    public AiCaseIndexBuildResult? PastCases { get; init; }

    public AiManualIndexBuildResult? Manuals { get; init; }

    public AiOfficialDocumentIndexBuildResult? OfficialDocs { get; init; }

    public EmbeddingIndexUpdateResult? Embeddings { get; init; }
}
