using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Llm;

public static class OllamaRequestBuilder
{
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
            },
        };
    }
}
