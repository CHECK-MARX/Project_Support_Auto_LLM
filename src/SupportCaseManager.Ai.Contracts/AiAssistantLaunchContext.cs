using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class AiAssistantLaunchContext
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("baseFolder")]
    public string BaseFolder { get; init; } = string.Empty;

    [JsonPropertyName("closeFolder")]
    public string CloseFolder { get; init; } = string.Empty;

    [JsonPropertyName("caseFolderPath")]
    public string CaseFolderPath { get; init; } = string.Empty;

    [JsonPropertyName("companyName")]
    public string CompanyName { get; init; } = string.Empty;

    [JsonPropertyName("customerName")]
    public string CustomerName { get; init; } = string.Empty;

    [JsonPropertyName("supportNumber")]
    public string SupportNumber { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("receptionDate")]
    public DateOnly? ReceptionDate { get; init; }

    [JsonPropertyName("noteKind")]
    public string NoteKind { get; init; } = string.Empty;

    [JsonPropertyName("noteFilePath")]
    public string NoteFilePath { get; init; } = string.Empty;

    [JsonPropertyName("selectedText")]
    public string SelectedText { get; init; } = string.Empty;

    [JsonPropertyName("currentNoteText")]
    public string CurrentNoteText { get; init; } = string.Empty;

    [JsonPropertyName("inquiryText")]
    public string InquiryText { get; init; } = string.Empty;

    [JsonPropertyName("additionalInstruction")]
    public string AdditionalInstruction { get; init; } = string.Empty;
}
