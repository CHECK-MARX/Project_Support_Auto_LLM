namespace SupportCaseManager.AiAssistant.App.Launch;

public sealed record class CommandLineOptions
{
    public string? ContextFilePath { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
