namespace SupportCaseManager.Ai.Core.Llm;

public sealed record class LlmGenerationResult
{
    public string Content { get; init; } = string.Empty;

    public string? Thinking { get; init; }

    public string? DoneReason { get; init; }

    public long? TotalDuration { get; init; }

    public int? EvalCount { get; init; }

    public int? PromptEvalCount { get; init; }

    public bool ThinkDisabledSent { get; init; }

    public bool ContentReturned => !string.IsNullOrWhiteSpace(Content);

    public bool ThinkingReturned => !string.IsNullOrWhiteSpace(Thinking);

    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}
