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

    public int CommandHeavyManualIncludedCount { get; init; }

    public int ArchivesScannedCount { get; init; }

    public int ZipFileCount { get; init; }

    public int ZipEntryCount { get; init; }

    public int SupportedZipEntryCount { get; init; }

    public int IndexedZipEntryCount { get; init; }

    public int SkippedZipEntryCount { get; init; }

    public int DuplicateZipEntryCount { get; init; }

    public int EncryptedZipCount { get; init; }

    public int CorruptZipCount { get; init; }

    public int UnsafeArchivePathRejectedCount { get; init; }

    public int ArchiveSizeLimitExceededCount { get; init; }

    public int IndexedFileCount { get; init; }

    public int IndexedChunkCount { get; init; }

    public int PageNumberChunkCount { get; init; }

    public int SectionTitleChunkCount { get; init; }

    public int PageAndSectionChunkCount { get; init; }

    public int ZipDerivedChunkCount { get; init; }

    public int ErrorCount { get; init; }

    public int AddedFileCount { get; init; }

    public int ChangedFileCount { get; init; }

    public int DeletedFileCount { get; init; }

    public int UnchangedFileCount { get; init; }

    public string IndexFilePath { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, int> UnsupportedExtensionCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> UnsupportedDocumentExtensionCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> OutOfScopeExtensionCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}
