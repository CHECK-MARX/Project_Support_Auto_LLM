using SupportCaseManager.Ai.Core.Artifacts;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed class ExcelTranslationEntryViewModel : ObservableObject
{
    private string translatedText = string.Empty;

    public ExcelTranslationEntryViewModel(ExcelTranslationEntry entry)
    {
        Entry = entry;
    }

    public ExcelTranslationEntry Entry { get; }
    public string Sheet => Entry.Sheet;
    public string Cell => Entry.TargetKind switch
    {
        ExcelTranslationTargetKind.SheetName => "シート名",
        ExcelTranslationTargetKind.DrawingText => $"図形段落 {Entry.DrawingParagraphIndex + 1}",
        _ => Entry.Cell,
    };
    public string TargetKindText => Entry.TargetKind switch
    {
        ExcelTranslationTargetKind.SheetName => "シート名",
        ExcelTranslationTargetKind.DrawingText => "図形テキスト",
        _ => "セル",
    };
    public string SourceText => Entry.SourceText;
    public string NumberFormat => Entry.NumberFormat;
    public string FormulaText => Entry.IsFormula ? "数式" : string.Empty;
    public string CommentText => Entry.HasComment ? "あり" : string.Empty;
    public string MergedRange => Entry.MergedRange ?? string.Empty;
    public string TargetText => Entry.ShouldTranslate ? "翻訳対象" : Entry.SkipReason;

    public string TranslatedText
    {
        get => translatedText;
        set => SetProperty(ref translatedText, value);
    }
}
