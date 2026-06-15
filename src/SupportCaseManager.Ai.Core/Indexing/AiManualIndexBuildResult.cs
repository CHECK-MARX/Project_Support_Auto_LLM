namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record class AiManualIndexBuildResult
{
    public int ScannedFileCount { get; init; }

    public int SupportedFileCount { get; init; }

    public int UnsupportedFileCount { get; init; }

    public int ContentExcludedFileCount { get; init; }

    public int UnsupportedDocumentFileCount { get; init; }

    public int OutOfScopeFileCount { get; init; }

    public int OtherUnsupportedFileCount { get; init; }

    public int EmptyFileSkippedCount { get; init; }

    public int ReadFailureCount { get; init; }

    public int DuplicateFileSkippedCount { get; init; }

    public int IndexedFileCount { get; init; }

    public int IndexedChunkCount { get; init; }

    public int ErrorCount { get; init; }

    public string IndexFilePath { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, int> UnsupportedExtensionCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> UnsupportedDocumentExtensionCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> OutOfScopeExtensionCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
