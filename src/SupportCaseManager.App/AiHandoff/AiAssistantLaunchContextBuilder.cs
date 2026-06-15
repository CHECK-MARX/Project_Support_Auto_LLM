using System;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.App.AiHandoff;

public sealed class AiAssistantLaunchContextBuilder : IAiAssistantLaunchContextBuilder
{
    public const string SourceName = "SupportCaseManager.App";

    public AiAssistantLaunchContext BuildFromCurrentState(AiAssistantCurrentState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var selectedText = Normalize(state.SelectedText);
        var currentNoteText = Normalize(state.CurrentNoteText);
        var inquiryText = FirstNonEmpty(state.InquiryText, selectedText, currentNoteText);

        return new AiAssistantLaunchContext
        {
            Source = SourceName,
            ProductName = Normalize(state.ProductName),
            BaseFolder = Normalize(state.BaseFolder),
            CloseFolder = Normalize(state.CloseFolder),
            CaseFolderPath = Normalize(state.CaseFolderPath),
            CompanyName = Normalize(state.CompanyName),
            CustomerName = Normalize(state.CustomerName),
            SupportNumber = Normalize(state.SupportNumber),
            Status = Normalize(state.Status),
            ReceptionDate = state.ReceptionDate,
            NoteKind = Normalize(state.NoteKind),
            NoteFilePath = Normalize(state.NoteFilePath),
            SelectedText = selectedText,
            CurrentNoteText = currentNoteText,
            InquiryText = inquiryText,
            AdditionalInstruction = Normalize(state.AdditionalInstruction),
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = Normalize(value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return string.Empty;
    }

    private static string Normalize(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }
}
