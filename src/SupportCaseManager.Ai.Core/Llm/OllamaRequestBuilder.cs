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
        if (!thinkDisabled)
        {
            return new
            {
                model = settings.ChatModel,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
                stream = false,
                format = "json",
                options = new
                {
                    temperature = settings.Temperature,
                    num_predict = settings.MaxOutputTokens,
                    num_ctx = EffectiveContextWindowTokens(settings),
                },
            };
        }

        return new
        {
            model = settings.ChatModel,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            stream = false,
            think = false,
            format = "json",
            options = new
            {
                temperature = settings.Temperature,
                num_predict = settings.MaxOutputTokens,
                num_ctx = EffectiveContextWindowTokens(settings),
            },
        };
    }
}
