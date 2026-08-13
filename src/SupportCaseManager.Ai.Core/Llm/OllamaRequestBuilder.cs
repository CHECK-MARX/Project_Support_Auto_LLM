using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Llm;

public static class OllamaRequestBuilder
{
    private static int EffectiveContextWindowTokens(LlmProviderSettings settings)
    {
        return settings.ContextWindowTokens > 0
            ? settings.ContextWindowTokens
            : LlmProviderSettings.DefaultContextWindowTokens;
    }

    public static object BuildChatRequestBody(
        LlmProviderSettings settings,
        string systemPrompt,
        string userPrompt,
        bool thinkDisabled)
    {
        var request = new Dictionary<string, object?>
        {
            ["model"] = settings.ChatModel,
            ["messages"] = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            ["stream"] = false,
            ["options"] = new
            {
                temperature = settings.Temperature,
                num_predict = settings.MaxOutputTokens,
                num_ctx = EffectiveContextWindowTokens(settings),
            },
        };

        var structuredOutputMode = settings.StructuredOutputMode;
        if (string.IsNullOrWhiteSpace(structuredOutputMode) ||
            string.Equals(structuredOutputMode, StructuredOutputModes.Json, StringComparison.OrdinalIgnoreCase))
        {
            request["format"] = "json";
        }

        if (thinkDisabled)
        {
            request["think"] = false;
        }

        return request;
    }
}
