namespace SupportCaseManager.Ai.Core.Llm;

public sealed record class OllamaConnectionCheckResult
{
    public bool IsSuccess { get; init; }

    public string Endpoint { get; init; } = string.Empty;

    public string? SelectedModel { get; init; }

    public IReadOnlyList<string> AvailableModels { get; init; } = [];

    public bool SelectedModelExists { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? ErrorCode { get; init; }

    public bool ChatTestAttempted { get; init; }

    public bool ChatTestSuccess { get; init; }

    public bool ChatContentReturned { get; init; }

    public bool ChatThinkingReturned { get; init; }

    public string? ChatDoneReason { get; init; }

    public long? ChatTotalDuration { get; init; }

    public string? ChatTestMessage { get; init; }

    public IReadOnlyList<string> ChatTestWarnings { get; init; } = [];
}
