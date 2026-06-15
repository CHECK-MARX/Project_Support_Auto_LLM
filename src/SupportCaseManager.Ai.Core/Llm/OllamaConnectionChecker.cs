using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Llm;

public sealed class OllamaConnectionChecker : IOllamaConnectionChecker
{
    private const int DefaultTimeoutSeconds = 10;
    private const int ChatTestNumPredict = 50;
    private const string ChatTestPrompt = "Reply with exactly this text: OK";

    private readonly HttpClient httpClient;

    public OllamaConnectionChecker()
        : this(new HttpClient())
    {
    }

    public OllamaConnectionChecker(HttpMessageHandler handler)
        : this(new HttpClient(handler))
    {
    }

    public OllamaConnectionChecker(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<OllamaConnectionCheckResult> CheckAsync(
        LlmProviderSettings settings,
        bool disableThinking = true,
        CancellationToken cancellationToken = default)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var endpoint = settings.Endpoint?.Trim() ?? string.Empty;
        if (!TryBuildTagsUri(endpoint, out var tagsUri))
        {
            return Failure(endpoint, settings.ChatModel, "Endpointの形式が正しくありません。", "InvalidEndpoint");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds > 0
            ? settings.TimeoutSeconds
            : DefaultTimeoutSeconds));

        try
        {
            using var response = await httpClient.GetAsync(tagsUri, timeoutCts.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Failure(endpoint, settings.ChatModel, $"OllamaがHTTP {(int)response.StatusCode}を返しました。", "HttpError");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            var availableModels = await ParseModelNamesAsync(stream, timeoutCts.Token);
            var selectedModelExists = ModelExists(availableModels, settings.ChatModel);

            var message = selectedModelExists
                ? $"接続成功。利用可能モデル数: {availableModels.Count}。選択モデルは存在します。"
                : $"接続成功。利用可能モデル数: {availableModels.Count}。選択モデルが見つかりません。モデルをpull済みか確認してください。";

            var chatTest = await RunChatTestAsync(settings, disableThinking, endpoint, timeoutCts.Token);

            return new OllamaConnectionCheckResult
            {
                IsSuccess = true,
                Endpoint = endpoint,
                SelectedModel = settings.ChatModel,
                AvailableModels = availableModels,
                SelectedModelExists = selectedModelExists,
                Message = message,
                ChatTestAttempted = chatTest.Attempted,
                ChatTestSuccess = chatTest.Success,
                ChatContentReturned = chatTest.ContentReturned,
                ChatThinkingReturned = chatTest.ThinkingReturned,
                ChatDoneReason = chatTest.DoneReason,
                ChatTotalDuration = chatTest.TotalDuration,
                ChatTestMessage = chatTest.Message,
                ChatTestWarnings = chatTest.Warnings,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(endpoint, settings.ChatModel, "接続確認がタイムアウトしました。Ollamaが起動しているか、Endpointが正しいか確認してください。", "Timeout");
        }
        catch (HttpRequestException ex)
        {
            return Failure(endpoint, settings.ChatModel, $"Ollamaへ接続できません。Ollamaが起動しているか確認してください。{ex.Message}", "ConnectionError");
        }
        catch (JsonException ex)
        {
            return Failure(endpoint, settings.ChatModel, $"Ollamaのモデル一覧JSONを解析できませんでした。{ex.Message}", "InvalidJson");
        }
        catch (IOException ex)
        {
            return Failure(endpoint, settings.ChatModel, $"Ollama応答の読み取りに失敗しました。{ex.Message}", "ReadError");
        }
    }

    internal async Task<ChatTestOutcome> RunChatTestAsync(
        LlmProviderSettings settings,
        bool disableThinking,
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (!TryBuildChatUri(endpoint, out var chatUri))
        {
            return new ChatTestOutcome
            {
                Attempted = false,
                Message = "Chat test skipped: invalid endpoint.",
            };
        }

        if (string.IsNullOrWhiteSpace(settings.ChatModel))
        {
            return new ChatTestOutcome
            {
                Attempted = false,
                Message = "Chat test skipped: no model selected.",
            };
        }

        var thinkDisabled = OllamaThinkingHelper.ShouldDisableThinking(settings.ChatModel, disableThinking);
        var userPrompt = OllamaThinkingHelper.ApplyNoThinkPrefix(ChatTestPrompt, settings.ChatModel, disableThinking);
        var requestBody = thinkDisabled
            ? new
            {
                model = settings.ChatModel,
                stream = false,
                think = false,
                messages = new[] { new { role = "user", content = userPrompt } },
                options = new { temperature = 0.2, num_predict = ChatTestNumPredict },
            }
            : (object)new
            {
                model = settings.ChatModel,
                stream = false,
                messages = new[] { new { role = "user", content = userPrompt } },
                options = new { temperature = 0.2, num_predict = ChatTestNumPredict },
            };

        try
        {
            using var response = await httpClient.PostAsJsonAsync(chatUri, requestBody, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ChatTestOutcome
                {
                    Attempted = true,
                    Success = false,
                    Message = $"Chat test: failure (HTTP {(int)response.StatusCode})",
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var generation = await OllamaClient.ParseChatResponseAsync(stream, thinkDisabled, cancellationToken);
            var warnings = new List<string>(generation.Diagnostics);
            var success = generation.ContentReturned && string.Equals(generation.Content.Trim(), "OK", StringComparison.Ordinal);

            if (generation.ThinkingReturned && !generation.ContentReturned)
            {
                warnings.Add("Chat test: thinking returned without content.");
            }

            if (string.Equals(generation.DoneReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("Chat test: done_reason=length");
            }

            if (!success && generation.ContentReturned)
            {
                warnings.Add($"Chat test: unexpected content '{Truncate(generation.Content)}'");
            }

            return new ChatTestOutcome
            {
                Attempted = true,
                Success = success,
                ContentReturned = generation.ContentReturned,
                ThinkingReturned = generation.ThinkingReturned,
                DoneReason = generation.DoneReason,
                TotalDuration = generation.TotalDuration,
                Message = success ? "Chat test: success" : "Chat test: failure",
                Warnings = warnings,
            };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("thinking", StringComparison.OrdinalIgnoreCase))
        {
            return new ChatTestOutcome
            {
                Attempted = true,
                Success = false,
                ThinkingReturned = true,
                Message = "Chat test: failure",
                Warnings = [ex.Message],
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ChatTestOutcome
            {
                Attempted = true,
                Success = false,
                Message = $"Chat test: failure ({ex.GetType().Name})",
                Warnings = [ex.Message],
            };
        }
    }

    internal sealed record ChatTestOutcome
    {
        public bool Attempted { get; init; }

        public bool Success { get; init; }

        public bool ContentReturned { get; init; }

        public bool ThinkingReturned { get; init; }

        public string? DoneReason { get; init; }

        public long? TotalDuration { get; init; }

        public string? Message { get; init; }

        public IReadOnlyList<string> Warnings { get; init; } = [];
    }

    private static bool TryBuildTagsUri(string endpoint, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        uri = new Uri(baseUri, "api/tags");
        return uri.Scheme is "http" or "https";
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

    private static async Task<IReadOnlyList<string>> ParseModelNamesAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("models", out var modelsElement) ||
            modelsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<string>();
        foreach (var modelElement in modelsElement.EnumerateArray())
        {
            var modelName = GetStringProperty(modelElement, "name")
                ?? GetStringProperty(modelElement, "model");
            if (!string.IsNullOrWhiteSpace(modelName))
            {
                models.Add(modelName);
            }
        }

        return models;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool ModelExists(IReadOnlyList<string> availableModels, string? selectedModel)
    {
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            return false;
        }

        return availableModels.Any(model =>
            string.Equals(model, selectedModel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(RemoveTag(model), selectedModel, StringComparison.OrdinalIgnoreCase));
    }

    private static string RemoveTag(string modelName)
    {
        var tagSeparatorIndex = modelName.IndexOf(':', StringComparison.Ordinal);
        return tagSeparatorIndex > 0 ? modelName[..tagSeparatorIndex] : modelName;
    }

    private static string Truncate(string value)
    {
        return value.Length <= 40 ? value : value[..40] + "...";
    }

    private static OllamaConnectionCheckResult Failure(
        string endpoint,
        string? selectedModel,
        string message,
        string errorCode)
    {
        return new OllamaConnectionCheckResult
        {
            IsSuccess = false,
            Endpoint = endpoint,
            SelectedModel = selectedModel,
            Message = message,
            ErrorCode = errorCode,
        };
    }
}
