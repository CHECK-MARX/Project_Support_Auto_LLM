using SupportCaseManager.Ai.Core.Artifacts;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed class ArtifactTranslationPreviewItemViewModel : ObservableObject
{
    private string translatedText = string.Empty;

    public ArtifactTranslationPreviewItemViewModel(
        string key,
        string kind,
        string location,
        string sourceText,
        bool shouldTranslate,
        string skipReason)
    {
        Key = key;
        Kind = kind;
        Location = location;
        SourceText = sourceText;
        ShouldTranslate = shouldTranslate;
        SkipReason = skipReason;
    }

    public string Key { get; }
    public string Kind { get; }
    public string Location { get; }
    public string SourceText { get; }
    public bool ShouldTranslate { get; }
    public string SkipReason { get; }
    public string TargetText => ShouldTranslate ? "翻訳対象" : SkipReason;

    public string TranslatedText
    {
        get => translatedText;
        set => SetProperty(ref translatedText, value);
    }

    public static ArtifactTranslationPreviewItemViewModel FromExcel(ExcelTranslationEntry entry) =>
        new(
            $"{entry.Sheet}\u001f{entry.Cell}",
            entry.TargetKind switch
            {
                ExcelTranslationTargetKind.SheetName => "シート名",
                ExcelTranslationTargetKind.DrawingText => "図形テキスト",
                _ => "セル",
            },
            entry.TargetKind switch
            {
                ExcelTranslationTargetKind.SheetName => $"{entry.Sheet} / シート名",
                ExcelTranslationTargetKind.DrawingText => $"{entry.Sheet} / 図形段落 {entry.DrawingParagraphIndex + 1}",
                _ => $"{entry.Sheet}!{entry.Cell}",
            },
            entry.SourceText,
            entry.ShouldTranslate,
            entry.SkipReason);

    public static ArtifactTranslationPreviewItemViewModel FromText(ArtifactTextTranslationEntry entry) =>
        new(entry.Key, "テキスト", entry.Location, entry.SourceText, entry.ShouldTranslate, entry.SkipReason);
}
