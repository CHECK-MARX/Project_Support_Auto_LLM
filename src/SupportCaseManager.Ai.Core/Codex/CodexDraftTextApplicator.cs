namespace SupportCaseManager.Ai.Core.Codex;

public enum CodexDraftApplyMode
{
    Overwrite,
    Append,
}

public static class CodexDraftTextApplicator
{
    public static string Apply(string? currentText, string incomingText, CodexDraftApplyMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incomingText);
        if (mode == CodexDraftApplyMode.Append && !string.IsNullOrWhiteSpace(currentText))
        {
            return $"{currentText.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{incomingText.Trim()}";
        }

        return incomingText.Trim();
    }
}
