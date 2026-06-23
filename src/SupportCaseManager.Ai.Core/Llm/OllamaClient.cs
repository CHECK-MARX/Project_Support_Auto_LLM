using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Prompts;

namespace SupportCaseManager.Ai.Core.Llm;

/// <summary>
/// Calls the local Ollama LLM API for RAG-based support answer generation.
/// The selected evidence is included in the prompt before this AI client sends it to the local model.
/// </summary>
public sealed class OllamaClient : ILlmClient
{
    private const int DefaultTimeoutSeconds = 120;
    private const int ThinkingRetryMinimumOutputTokens = 800;
    private const string ThinkingRetrySystemInstruction = """
        重要: thinking、reasoning、推論過程は出力しないでください。
        Ollamaの message.thinking ではなく、必ず message.content に最終回答だけを出力してください。
        message.content を空にしてはいけません。
        """;
    private const string ThinkingRetryUserInstruction = """
        上記指示を厳守してください。thinkingを出力せず、message.content に最終回答JSONだけを返してください。
        """;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient httpClient;

    public OllamaClient()
        : this(new HttpClient())
    {
    }

    public OllamaClient(HttpMessageHandler handler)
        : this(new HttpClient(handler))
    {
    }

    public OllamaClient(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<LlmGenerationResult> GenerateAsync(
        PromptMessages messages,
        LlmProviderSettings settings,
        bool disableThinking = true,
        CancellationToken cancellationToken = default)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var endpoint = settings.Endpoint?.Trim() ?? string.Empty;
        if (!TryBuildChatUri(endpoint, out var uri))
        {
            throw new InvalidOperationException("Ollama endpoint is invalid. Check the Endpoint setting.");
        }

        var configuredTimeoutSeconds = settings.TimeoutSeconds > 0
            ? settings.TimeoutSeconds
            : DefaultTimeoutSeconds;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuredTimeoutSeconds));
        var stopwatch = Stopwatch.StartNew();

        var thinkDisabled = OllamaThinkingHelper.ShouldDisableThinking(settings.ChatModel, disableThinking);
        var systemPrompt = OllamaThinkingHelper.ApplyNoThinkPrefix(messages.SystemPrompt, settings.ChatModel, disableThinking);
        var userPrompt = OllamaThinkingHelper.ApplyNoThinkPrefix(messages.UserPrompt, settings.ChatModel, disableThinking);

        var requestBody = OllamaRequestBuilder.BuildChatRequestBody(settings, systemPrompt, userPrompt, thinkDisabled);

        try
        {
            try
            {
                return await SendChatRequestAsync(uri, requestBody, thinkDisabled, timeoutCts.Token);
            }
            catch (OllamaThinkingOnlyException) when (thinkDisabled)
            {
                var retrySettings = BuildThinkingRetrySettings(settings);
                var retryRequestBody = OllamaRequestBuilder.BuildChatRequestBody(
                    retrySettings,
                    BuildThinkingRetrySystemPrompt(systemPrompt),
                    BuildThinkingRetryUserPrompt(userPrompt),
                    thinkDisabled);

                var retryResult = await SendChatRequestAsync(uri, retryRequestBody, thinkDisabled, timeoutCts.Token);
                return retryResult with
                {
                    Diagnostics = new[]
                    {
                        "Ollamaがthinking出力のみを返したため、強いno-think指示で1回リトライしました。",
                    }
                    .Concat(retryResult.Diagnostics)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                };
            }
        }
        catch (OllamaThinkingOnlyException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            throw new InvalidOperationException(BuildTimeoutMessage(
                configuredTimeoutSeconds,
                stopwatch.Elapsed,
                settings.ChatModel,
                messages,
                thinkDisabled));
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Ollama /api/chat could not connect. Check that Ollama is running and the Endpoint is correct.", ex);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Ollama /api/chat response JSON could not be parsed.", ex);
        }
    }

    private async Task<LlmGenerationResult> SendChatRequestAsync(
        Uri uri,
        object requestBody,
        bool thinkDisabled,
        CancellationToken cancellationToken)
    {
        // Use explicit UTF-8 JSON for Japanese support prompts sent to the local Ollama /api/chat endpoint.
        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(uri, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Ollama /api/chat returned HTTP {(int)response.StatusCode}. {Truncate(responseText)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ParseChatResponseAsync(stream, thinkDisabled, cancellationToken);
    }

    internal static async Task<LlmGenerationResult> ParseChatResponseAsync(
        Stream stream,
        bool thinkDisabledSent,
        CancellationToken cancellationToken)
    {
        var chatResponse = await JsonSerializer.DeserializeAsync<OllamaChatResponse>(
            stream,
            JsonOptions,
            cancellationToken);

        // Ollama /api/chat wraps the model output in message.content.
        // The outer Ollama response is not the support answer DTO.
        var content = chatResponse?.Message?.Content ?? string.Empty;
        var thinking = chatResponse?.Message?.Thinking;
        var doneReason = chatResponse?.DoneReason;
        var totalDuration = chatResponse?.TotalDuration;
        var evalCount = chatResponse?.EvalCount;
        var promptEvalCount = chatResponse?.PromptEvalCount;

        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(thinking))
        {
            diagnostics.Add("モデルがthinking出力のみを返しました。No-think設定または think:false が有効か確認してください。");
            throw new OllamaThinkingOnlyException(diagnostics[0]);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Ollama response message.content is empty.");
        }

        if (string.Equals(doneReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add("Ollama応答が最大出力トークンで打ち切られました。最大出力トークンを増やすか、プロンプトを短くしてください。");
        }

        return new LlmGenerationResult
        {
            Content = content,
            Thinking = thinking,
            DoneReason = doneReason,
            TotalDuration = totalDuration,
            EvalCount = evalCount,
            PromptEvalCount = promptEvalCount,
            ThinkDisabledSent = thinkDisabledSent,
            Diagnostics = diagnostics,
        };
    }

    private static bool TryBuildChatUri(string endpoint, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        uri = new Uri(baseUri, "api/chat");
        return uri.Scheme is "http" or "https";
    }

    private static string BuildThinkingRetrySystemPrompt(string systemPrompt)
    {
        return $"{ThinkingRetrySystemInstruction}{Environment.NewLine}{systemPrompt}";
    }

    private static string BuildThinkingRetryUserPrompt(string userPrompt)
    {
        return $"{ThinkingRetryUserInstruction}{Environment.NewLine}{userPrompt}";
    }

    private static LlmProviderSettings BuildThinkingRetrySettings(LlmProviderSettings settings)
    {
        if (!OllamaThinkingHelper.IsGptOssModel(settings.ChatModel) ||
            settings.MaxOutputTokens >= ThinkingRetryMinimumOutputTokens)
        {
            return settings;
        }

        return settings with
        {
            MaxOutputTokens = ThinkingRetryMinimumOutputTokens,
        };
    }

    private static string Truncate(string value)
    {
        const int maxLength = 300;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static string BuildTimeoutMessage(
        int configuredTimeoutSeconds,
        TimeSpan elapsed,
        string model,
        PromptMessages messages,
        bool thinkDisabled)
    {
        var promptChars = messages.Diagnostics.FinalPromptChars > 0
            ? messages.Diagnostics.FinalPromptChars
            : (messages.SystemPrompt.Length + messages.UserPrompt.Length);

        return string.Join(Environment.NewLine,
        [
            "Ollama /api/chat timed out.",
            $"configured timeout seconds: {configuredTimeoutSeconds}",
            $"elapsed seconds: {elapsed.TotalSeconds:0.0}",
            $"model: {model}",
            $"prompt chars: {promptChars}",
            $"evidence count: {messages.Diagnostics.EvidenceCount}",
            $"think:false sent: {(thinkDisabled ? "yes" : "no")}",
        ]);
    }
}

internal sealed class OllamaThinkingOnlyException : InvalidOperationException
{
    public OllamaThinkingOnlyException(string message)
        : base(message)
    {
    }
}
