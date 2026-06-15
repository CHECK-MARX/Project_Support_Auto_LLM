namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record class AiCaseIndexBuildResult
{
    public int ScannedCaseFolderCount { get; init; }

    public int ScannedNoteFileCount { get; init; }

    public int EmptyNoteSkippedCount { get; init; }

    public int SupportNumberExtractedCount { get; init; }

    public int MissingSupportNumberCount { get; init; }

    public int NoteKindExtractedCount { get; init; }

    public int UnknownNoteKindCount { get; init; }

    public int IndexedCaseCount { get; init; }

    public int IndexedNoteCount { get; init; }

    public int ErrorCount { get; init; }

    public string IndexFilePath { get; init; } = string.Empty;

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
