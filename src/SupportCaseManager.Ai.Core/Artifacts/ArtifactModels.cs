namespace SupportCaseManager.Ai.Core.Artifacts;

public enum ArtifactKind
{
    ExcelEnglishTranslation,
    CsvEnglishTranslation,
    TextEnglishTranslation,
    WordEnglishTranslation,
}

public enum ArtifactFormat
{
    Unsupported,
    ExcelWorkbook,
    WordDocument,
    Csv,
    PlainText,
    Markdown,
}

public enum ExcelTranslationTargetKind
{
    Cell,
    SheetName,
    DrawingText,
}

public sealed record ArtifactCreationRequest
{
    public string CaseFolder { get; init; } = string.Empty;
    public string SourceFilePath { get; init; } = string.Empty;
    public string DestinationFolder { get; init; } = string.Empty;
    public string OutputFileName { get; init; } = "Inquiry_Details_EN.xlsx";
    public string ProductName { get; init; } = string.Empty;
    public string UserInstruction { get; init; } = string.Empty;
}

public sealed record ArtifactTextTranslationEntry
{
    public string Key { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string SourceText { get; init; } = string.Empty;
    public bool ShouldTranslate { get; init; }
    public string SkipReason { get; init; } = string.Empty;
}

public sealed record ArtifactTextTranslationValue
{
    public string Key { get; init; } = string.Empty;
    public string SourceText { get; init; } = string.Empty;
    public string TranslatedText { get; init; } = string.Empty;
}

public sealed record ArtifactTextTranslationPlan
{
    public IReadOnlyList<ArtifactTextTranslationEntry> Entries { get; init; } = [];
    public string EncodingName { get; init; } = "utf-8";
    public bool HasByteOrderMark { get; init; }
    public string NewLine { get; init; } = "\n";
    public char CsvDelimiter { get; init; } = ',';
    public bool CsvEndsWithNewLine { get; init; }
    public int TranslatableCount => Entries.Count(static item => item.ShouldTranslate);
    public int UnchangedCount => Entries.Count(static item => !item.ShouldTranslate);
}

public sealed record ExcelTranslationEntry
{
    public ExcelTranslationTargetKind TargetKind { get; init; } = ExcelTranslationTargetKind.Cell;
    public string Sheet { get; init; } = string.Empty;
    public string Cell { get; init; } = string.Empty;
    public int DrawingParagraphIndex { get; init; } = -1;
    public string SourceText { get; init; } = string.Empty;
    public bool IsFormula { get; init; }
    public string NumberFormat { get; init; } = string.Empty;
    public bool HasComment { get; init; }
    public string? MergedRange { get; init; }
    public bool ShouldTranslate { get; init; }
    public string SkipReason { get; init; } = string.Empty;
}

public sealed record ExcelTranslationValue
{
    public string Sheet { get; init; } = string.Empty;
    public string Cell { get; init; } = string.Empty;
    public string SourceText { get; init; } = string.Empty;
    public string TranslatedText { get; init; } = string.Empty;
}

public sealed record ExcelTextExtractionResult
{
    public IReadOnlyList<ExcelTranslationEntry> Entries { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public int SheetCount { get; init; }
}

public sealed record ExcelTranslationPlan
{
    public IReadOnlyList<ExcelTranslationEntry> Entries { get; init; } = [];
    public int TranslatableCount => Entries.Count(static item => item.ShouldTranslate);
    public int UnchangedCount => Entries.Count(static item => !item.ShouldTranslate);
}

public sealed record ArtifactCreationPlan
{
    public Guid PlanId { get; init; } = Guid.NewGuid();
    public ArtifactKind Kind { get; init; } = ArtifactKind.ExcelEnglishTranslation;
    public ArtifactFormat Format { get; init; } = ArtifactFormat.ExcelWorkbook;
    public ArtifactCreationRequest Request { get; init; } = new();
    public string CaseFolderFullPath { get; init; } = string.Empty;
    public string SourceFullPath { get; init; } = string.Empty;
    public string DestinationFullPath { get; init; } = string.Empty;
    public string OutputFullPath { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public bool DestinationFolderWillBeCreated { get; init; }
    public bool OverwriteAllowed { get; init; }
    public bool SourceWillBeModified { get; init; }
    public ExcelTranslationPlan Excel { get; init; } = new();
    public ArtifactTextTranslationPlan Text { get; init; } = new();
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record ArtifactCreationResult
{
    public bool Succeeded { get; init; }
    public string OutputFilePath { get; init; } = string.Empty;
    public int TranslationTargetCount { get; init; }
    public int TranslatedCount { get; init; }
    public int UnchangedCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record ExcelTranslationParseResult
{
    public bool Succeeded { get; init; }
    public IReadOnlyList<ExcelTranslationValue> Values { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}
