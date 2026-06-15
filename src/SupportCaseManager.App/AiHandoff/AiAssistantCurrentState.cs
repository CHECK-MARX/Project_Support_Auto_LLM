using System;

namespace SupportCaseManager.App.AiHandoff;

public sealed record class AiAssistantCurrentState
{
    public string ProductName { get; init; } = string.Empty;

    public string BaseFolder { get; init; } = string.Empty;

    public string CloseFolder { get; init; } = string.Empty;

    public string CaseFolderPath { get; init; } = string.Empty;

    public string CompanyName { get; init; } = string.Empty;

    public string CustomerName { get; init; } = string.Empty;

    public string SupportNumber { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateOnly? ReceptionDate { get; init; }

    public string NoteKind { get; init; } = string.Empty;

    public string NoteFilePath { get; init; } = string.Empty;

    public string SelectedText { get; init; } = string.Empty;

    public string CurrentNoteText { get; init; } = string.Empty;

    public string InquiryText { get; init; } = string.Empty;

    public string AdditionalInstruction { get; init; } = string.Empty;
}
