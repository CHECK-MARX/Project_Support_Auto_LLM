using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Prompts;

namespace SupportCaseManager.Ai.Core.Llm;

public sealed class OllamaClient : ILlmClient
{
    private const int DefaultTimeoutSeconds = 120;

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
            using var response = await httpClient.PostAsJsonAsync(uri, requestBody, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                throw new InvalidOperationException(
                    $"Ollama /api/chat returned HTTP {(int)response.StatusCode}. {Truncate(responseText)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            return await ParseChatResponseAsync(stream, thinkDisabled, timeoutCts.Token);
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

    internal static async Task<LlmGenerationResult> ParseChatResponseAsync(
        Stream stream,
        bool thinkDisabledSent,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var content = ReadMessageString(root, "content");
        var thinking = ReadMessageString(root, "thinking");
        var doneReason = ReadStringProperty(root, "done_reason");
        var totalDuration = ReadLongProperty(root, "total_duration");
        var evalCount = ReadIntProperty(root, "eval_count");
        var promptEvalCount = ReadIntProperty(root, "prompt_eval_count");

        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(thinking))
        {
            diagnostics.Add("モデルがthinking出力のみを返しました。No-think設定または think:false が有効か確認してください。");
            throw new InvalidOperationException(diagnostics[0]);
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

    private static string ReadMessageString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty("message", out var messageElement) ||
            messageElement.ValueKind != JsonValueKind.Object ||
            !messageElement.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static long? ReadLongProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            _ => null,
        };
    }

    private static int? ReadIntProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            _ => null,
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
