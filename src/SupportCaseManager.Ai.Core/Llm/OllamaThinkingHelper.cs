namespace SupportCaseManager.Ai.Core.Llm;

public static class OllamaThinkingHelper
{
    public const string NoThinkPrefix = "/no_think";

    public static bool ShouldDisableThinking(string? modelName, bool disableThinkingSetting)
    {
        if (!disableThinkingSetting)
        {
            return false;
        }

        return true;
    }

    public static bool IsQwen3Model(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        var normalized = modelName.Trim();
        var tagSeparatorIndex = normalized.IndexOf(':', StringComparison.Ordinal);
        var baseName = tagSeparatorIndex > 0 ? normalized[..tagSeparatorIndex] : normalized;
        return baseName.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase);
    }

    public static string ApplyNoThinkPrefix(string prompt, string? modelName, bool disableThinkingSetting)
    {
        if (!ShouldDisableThinking(modelName, disableThinkingSetting) || !IsQwen3Model(modelName))
        {
            return prompt;
        }

        if (prompt.StartsWith(NoThinkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return prompt;
        }

        return $"{NoThinkPrefix}\n{prompt}";
    }
}
