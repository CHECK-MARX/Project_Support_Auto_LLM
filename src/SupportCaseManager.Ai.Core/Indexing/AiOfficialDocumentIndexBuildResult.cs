namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record class AiOfficialDocumentIndexBuildResult
{
    public string ProductName { get; init; } = string.Empty;

    public string IndexFilePath { get; init; } = string.Empty;

    public int SourceUrlCount { get; init; }

    public int DiscoveredUrlCount { get; init; }

    public int FetchSuccessCount { get; init; }

    public int FetchFailureCount { get; init; }

    public int SkippedUrlCount { get; init; }

    public int IndexedChunkCount { get; init; }

    public int MaxDepth { get; init; }

    public int MaxPages { get; init; }

    public int RequestDelayMs { get; init; }

    public int FetchTimeoutSeconds { get; init; }

    public int WarningCount => Warnings.Count;

    public IReadOnlyList<string> SourceUrls { get; init; } = [];

    public IReadOnlyList<string> DiscoveredUrls { get; init; } = [];

    public IReadOnlyList<string> RetrievedUrls { get; init; } = [];

    public IReadOnlyList<string> ImportantPageUrls { get; init; } = [];

    public IReadOnlyList<string> FailedUrls { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
