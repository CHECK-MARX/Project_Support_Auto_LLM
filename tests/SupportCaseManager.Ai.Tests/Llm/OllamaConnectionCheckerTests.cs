using System.Net;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Llm;

namespace SupportCaseManager.Ai.Tests.Llm;

public class OllamaConnectionCheckerTests
{
    [Fact]
    public async Task CheckAsync_ReadsModelsFromTagsJson()
    {
        var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(CreateDefaultResponse(request, """
            {
              "models": [
                { "name": "llama3.1:latest" },
                { "model": "mistral:latest" }
              ]
            }
            """)));
        var checker = new OllamaConnectionChecker(handler);

        var result = await checker.CheckAsync(CreateSettings());

        Assert.True(result.IsSuccess);
        Assert.Equal(["llama3.1:latest", "mistral:latest"], result.AvailableModels);
    }

    [Fact]
    public async Task CheckAsync_ReturnsSelectedModelExistsWhenModelIsAvailable()
    {
        var checker = new OllamaConnectionChecker(new StubHttpMessageHandler((request, _) => Task.FromResult(CreateDefaultResponse(request, """
            { "models": [ { "name": "llama3.1:latest" } ] }
            """))));

        var result = await checker.CheckAsync(CreateSettings(chatModel: "llama3.1"));

        Assert.True(result.IsSuccess);
        Assert.True(result.SelectedModelExists);
    }

    [Fact]
    public async Task CheckAsync_ReturnsSelectedModelMissingWhenModelIsUnavailable()
    {
        var checker = new OllamaConnectionChecker(new StubHttpMessageHandler((request, _) => Task.FromResult(CreateDefaultResponse(request, """
            { "models": [ { "name": "mistral:latest" } ] }
            """))));

        var result = await checker.CheckAsync(CreateSettings(chatModel: "llama3.1"));

        Assert.True(result.IsSuccess);
        Assert.False(result.SelectedModelExists);
    }

    [Fact]
    public async Task CheckAsync_ReturnsFailureForConnectionError()
    {
        var checker = new OllamaConnectionChecker(new StubHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused")));

        var result = await checker.CheckAsync(CreateSettings());

        Assert.False(result.IsSuccess);
        Assert.Equal("ConnectionError", result.ErrorCode);
        Assert.Contains("Ollama", result.Message);
    }

    [Fact]
    public async Task CheckAsync_ReturnsFailureForTimeout()
    {
        var checker = new OllamaConnectionChecker(new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateDefaultResponse(null!);
        }));

        var result = await checker.CheckAsync(CreateSettings(timeoutSeconds: 1));

        Assert.False(result.IsSuccess);
        Assert.Equal("Timeout", result.ErrorCode);
    }

    [Fact]
    public async Task CheckAsync_ReturnsFailureForInvalidJson()
    {
        var checker = new OllamaConnectionChecker(new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("not-json"),
                });
            }

            return Task.FromResult(CreateDefaultResponse(request));
        }));

        var result = await checker.CheckAsync(CreateSettings());

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidJson", result.ErrorCode);
    }

    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/api/tags")]
    [InlineData("http://localhost:11434/", "http://localhost:11434/api/tags")]
    public async Task CheckAsync_BuildsTagsUrlWithOrWithoutTrailingSlash(string endpoint, string expectedUrl)
    {
        Uri? requestedUri = null;
        var checker = new OllamaConnectionChecker(new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                requestedUri = request.RequestUri;
            }

            return Task.FromResult(CreateDefaultResponse(request));
        }));

        _ = await checker.CheckAsync(CreateSettings(endpoint: endpoint));

        Assert.Equal(expectedUrl, requestedUri?.ToString());
    }

    [Fact]
    public async Task CheckAsync_DoesNotUseApiKeyOrCustomerInformation()
    {
        HttpRequestMessage? requestMessage = null;
        var checker = new OllamaConnectionChecker(new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                requestMessage = request;
            }

            return Task.FromResult(CreateDefaultResponse(request));
        }));
        var settings = CreateSettings() with
        {
            ApiKeyEnvironmentVariable = "SUPPORT_AI_API_KEY",
        };

        _ = await checker.CheckAsync(settings);

        Assert.NotNull(requestMessage);
        Assert.Null(requestMessage.Headers.Authorization);
        Assert.DoesNotContain("SUPPORT_AI_API_KEY", requestMessage.RequestUri?.ToString() ?? string.Empty);
        Assert.Equal(HttpMethod.Get, requestMessage.Method);
    }

    private static LlmProviderSettings CreateSettings(
        string endpoint = "http://localhost:11434",
        string chatModel = "llama3.1",
        int timeoutSeconds = 120)
    {
        return new LlmProviderSettings
        {
            Endpoint = endpoint,
            ChatModel = chatModel,
            TimeoutSeconds = timeoutSeconds,
        };
    }

    private static HttpResponseMessage CreateDefaultResponse(HttpRequestMessage request, string tagsJson = """{ "models": [] }""")
    {
        if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase) == true)
        {
            return JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "OK" },
                  "done_reason": "stop"
                }
                """);
        }

        return JsonResponse(tagsJson);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
        {
            sendAsync = (request, _) => Task.FromResult(send(request));
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
