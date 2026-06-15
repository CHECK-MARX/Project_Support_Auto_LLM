namespace SupportCaseManager.Ai.Core.Prompts;

public sealed record class PromptMessages
{
    public string SystemPrompt { get; init; } = string.Empty;

    public string UserPrompt { get; init; } = string.Empty;

    public PromptDiagnostics Diagnostics { get; init; } = new();
}

public sealed record class PromptDiagnostics
{
    public int ConfiguredMaxPromptChars { get; init; }

    public int FinalPromptChars { get; init; }

    public int SystemChars { get; init; }

    public int UserPromptChars { get; init; }

    public int InquiryChars { get; init; }

    public int EvidenceChars { get; init; }

    public int EvidenceCount { get; init; }
}
