using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record class AiOfficialDocumentIndexDocument
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("builtAt")]
    public DateTimeOffset BuiltAt { get; init; }

    [JsonPropertyName("documents")]
    public IReadOnlyList<AiIndexedOfficialDocument> Documents { get; init; } = [];

    [JsonPropertyName("sourceUrls")]
    public IReadOnlyList<string> SourceUrls { get; init; } = [];

    [JsonPropertyName("discoveredUrls")]
    public IReadOnlyList<string> DiscoveredUrls { get; init; } = [];

    [JsonPropertyName("retrievedUrls")]
    public IReadOnlyList<string> RetrievedUrls { get; init; } = [];

    [JsonPropertyName("seedUrlCount")]
    public int SeedUrlCount { get; init; }

    [JsonPropertyName("discoveredUrlCount")]
    public int DiscoveredUrlCount { get; init; }

    [JsonPropertyName("fetchSuccessCount")]
    public int FetchSuccessCount { get; init; }

    [JsonPropertyName("fetchFailureCount")]
    public int FetchFailureCount { get; init; }

    [JsonPropertyName("skippedUrlCount")]
    public int SkippedUrlCount { get; init; }

    [JsonPropertyName("maxDepth")]
    public int MaxDepth { get; init; }

    [JsonPropertyName("maxPages")]
    public int MaxPages { get; init; }

    [JsonPropertyName("requestDelayMs")]
    public int RequestDelayMs { get; init; }

    [JsonPropertyName("fetchTimeoutSeconds")]
    public int FetchTimeoutSeconds { get; init; }

    [JsonPropertyName("importantPageUrls")]
    public IReadOnlyList<string> ImportantPageUrls { get; init; } = [];

    [JsonPropertyName("failedUrls")]
    public IReadOnlyList<string> FailedUrls { get; init; } = [];

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
