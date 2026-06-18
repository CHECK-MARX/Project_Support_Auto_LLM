using System.Net;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;

namespace SupportCaseManager.Ai.Tests.Llm;

public class OllamaClientTests
{
    [Fact]
    public async Task GenerateAsync_ReturnsMessageContentFromChatResponse()
    {
        var client = new OllamaClient(new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "model": "qwen3:4b",
              "message": { "role": "assistant", "content": "{\"customerReplyDraft\":\"回答\"}" },
              "done": true
            }
            """)));

        var result = await client.GenerateAsync(CreateMessages(), CreateSettings());

        Assert.Equal("{\"customerReplyDraft\":\"回答\"}", result.Content);
    }

    [Fact]
    public async Task GenerateAsync_SendsExpectedChatRequest()
    {
        string? requestJson = null;
        var client = new OllamaClient(new StubHttpMessageHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "{}" },
                  "done": true
                }
                """);
        }));

        _ = await client.GenerateAsync(CreateMessages(), CreateSettings(chatModel: "qwen3:4b"));

        Assert.NotNull(requestJson);
        using var document = JsonDocument.Parse(requestJson);
        var root = document.RootElement;
        Assert.Equal("qwen3:4b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("json", root.GetProperty("format").GetString());
        Assert.False(root.GetProperty("think").GetBoolean());

        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.StartsWith("/no_think", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.StartsWith("/no_think", messages[1].GetProperty("content").GetString());
        Assert.Contains("user prompt", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateAsync_SendsTemperatureNumPredictAndNumCtxOptions()
    {
        string? requestJson = null;
        var client = new OllamaClient(new StubHttpMessageHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "{}" },
                  "done": true
                }
                """);
        }));

        _ = await client.GenerateAsync(
            CreateMessages(),
            CreateSettings(temperature: 0.35, maxOutputTokens: 4096, contextWindowTokens: 8192));

        Assert.NotNull(requestJson);
        using var document = JsonDocument.Parse(requestJson);
        var options = document.RootElement.GetProperty("options");
        Assert.Equal(0.35, options.GetProperty("temperature").GetDouble());
        Assert.Equal(4096, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(8192, options.GetProperty("num_ctx").GetInt32());
    }

    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/api/chat")]
    [InlineData("http://localhost:11434/", "http://localhost:11434/api/chat")]
    public async Task GenerateAsync_BuildsChatUrlWithOrWithoutTrailingSlash(string endpoint, string expectedUrl)
    {
        Uri? requestedUri = null;
        var client = new OllamaClient(new StubHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri;
            return JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "{}" },
                  "done": true
                }
                """);
        }));

        _ = await client.GenerateAsync(CreateMessages(), CreateSettings(endpoint: endpoint));

        Assert.Equal(expectedUrl, requestedUri?.ToString());
    }

    [Fact]
    public async Task GenerateAsync_ThrowsClearExceptionForConnectionFailure()
    {
        var client = new OllamaClient(new StubHttpMessageHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("connection refused"))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateAsync(CreateMessages(), CreateSettings()));

        Assert.Contains("could not connect", exception.Message);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsClearExceptionForTimeout()
    {
        var client = new OllamaClient(new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "{}" },
                  "done": true
                }
                """);
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateAsync(CreateMessages(), CreateSettings(timeoutSeconds: 1)));

        Assert.Contains("timed out", exception.Message);
        Assert.Contains("configured timeout seconds: 1", exception.Message);
        Assert.Contains("model: qwen3:14b", exception.Message);
        Assert.Contains("prompt chars:", exception.Message);
        Assert.Contains("evidence count:", exception.Message);
        Assert.Contains("think:false sent: yes", exception.Message);
    }

    [Fact]
    public void Constructor_DisablesHttpClientFixedTimeout()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => JsonResponse("{}")));

        _ = new OllamaClient(httpClient);

        Assert.Equal(Timeout.InfiniteTimeSpan, httpClient.Timeout);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsClearExceptionForInvalidJson()
    {
        var client = new OllamaClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json"),
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateAsync(CreateMessages(), CreateSettings()));

        Assert.Contains("could not be parsed", exception.Message);
    }

    [Fact]
    public async Task GenerateAsync_ThrowsClearExceptionWhenMessageContentIsEmpty()
    {
        var client = new OllamaClient(new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "message": { "role": "assistant", "content": "" },
              "done": true
            }
            """)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateAsync(CreateMessages(), CreateSettings()));

        Assert.Contains("message.content is empty", exception.Message);
    }

    [Fact]
    public async Task GenerateAsync_DoesNotSendApiKey()
    {
        HttpRequestMessage? requestMessage = null;
        string? requestJson = null;
        var client = new OllamaClient(new StubHttpMessageHandler(async request =>
        {
            requestMessage = request;
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "{}" },
                  "done": true
                }
                """);
        }));
        var settings = CreateSettings() with
        {
            ApiKeyEnvironmentVariable = "SUPPORT_AI_API_KEY",
        };

        _ = await client.GenerateAsync(CreateMessages(), settings);

        Assert.NotNull(requestMessage);
        Assert.Equal("application/json", requestMessage.Content?.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", requestMessage.Content?.Headers.ContentType?.CharSet);
        Assert.Null(requestMessage.Headers.Authorization);
        Assert.DoesNotContain("SUPPORT_AI_API_KEY", requestMessage.RequestUri?.ToString() ?? string.Empty);
        Assert.DoesNotContain("SUPPORT_AI_API_KEY", requestJson);
    }

    private static PromptMessages CreateMessages()
    {
        return new PromptMessages
        {
            SystemPrompt = "system prompt",
            UserPrompt = "user prompt",
            Diagnostics = new PromptDiagnostics
            {
                FinalPromptChars = "system prompt".Length + "user prompt".Length,
                EvidenceCount = 0,
            },
        };
    }

    private static LlmProviderSettings CreateSettings(
        string endpoint = "http://localhost:11434",
        string chatModel = "qwen3:14b",
        double temperature = 0.2,
        int maxOutputTokens = 2048,
        int contextWindowTokens = 8192,
        int timeoutSeconds = 120)
    {
        return new LlmProviderSettings
        {
            Endpoint = endpoint,
            ChatModel = chatModel,
            Temperature = temperature,
            MaxOutputTokens = maxOutputTokens,
            ContextWindowTokens = contextWindowTokens,
            TimeoutSeconds = timeoutSeconds,
        };
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
        {
            sendAsync = (request, _) => Task.FromResult(send(request));
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync)
        {
            this.sendAsync = (request, _) => sendAsync(request);
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        {
            this.sendAsync = sendAsync;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return sendAsync(request, cancellationToken);
        }
    }
}
