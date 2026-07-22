using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed record CodexCaseSnapshot
{
    public Guid? ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string ProductPromptFilePath { get; init; } = string.Empty;
    public string SupportToolSettingsFilePath { get; init; } = string.Empty;
    public string SupportId { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ReceptionDate { get; init; } = string.Empty;
    public string CaseFolder { get; init; } = string.Empty;
    public string InquiryFile { get; init; } = string.Empty;
    public string InquiryText { get; init; } = string.Empty;
    public string CustomerReplyDraft { get; init; } = string.Empty;
    public string InternalMemo { get; init; } = string.Empty;
    public IReadOnlyList<SearchSource> Evidence { get; init; } = [];
}
