namespace SupportCaseManager.Ai.Core.Llm;

public sealed record OllamaModelCapabilityResult
{
    public bool CanGenerate { get; init; }

    public string Message { get; init; } = string.Empty;
}
