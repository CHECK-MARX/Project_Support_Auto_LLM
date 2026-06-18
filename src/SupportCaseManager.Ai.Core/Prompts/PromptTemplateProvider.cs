using System.Reflection;

namespace SupportCaseManager.Ai.Core.Prompts;

internal static class PromptTemplateProvider
{
    private const string SystemPromptFileName = "support-answer-system-prompt.md";
    private const string OutputPromptFileName = "support-answer-output-prompt.md";

    public static string SupportAnswerSystemPrompt => ReadEmbeddedPrompt(SystemPromptFileName);

    public static string SupportAnswerOutputPrompt => ReadEmbeddedPrompt(OutputPromptFileName);

    private static string ReadEmbeddedPrompt(string fileName)
    {
        var assembly = typeof(PromptTemplateProvider).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith($".Prompts.{fileName}", StringComparison.Ordinal));
        if (resourceName is null)
        {
            throw new InvalidOperationException($"Prompt template resource was not found: {fileName}");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Prompt template resource could not be opened: {fileName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
