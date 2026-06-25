using System.Net;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Llm;

namespace SupportCaseManager.Ai.Tests.Llm;

public sealed class OllamaConnectionCheckerChatTestTests
{
    [Fact]
    public async Task CheckAsync_AfterTagsSuccess_RunsShortChatTest()
    {
        var handler = new SequenceHttpMessageHandler(
            JsonResponse("""
                {
                  "models": [{ "name": "qwen3:8b" }]
                }
                """),
            JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "OK" },
                  "done_reason": "stop",
                  "total_duration": 1500000000
                }
                """));
        var checker = new OllamaConnectionChecker(handler);

        var result = await checker.CheckAsync(CreateSettings("qwen3:8b"), disableThinking: true);

        Assert.True(result.IsSuccess);
        Assert.True(result.ChatTestAttempted);
        Assert.True(result.ChatTestSuccess);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task CheckAsync_WithContentOk_MarksChatTestSuccess()
    {
        var handler = new SequenceHttpMessageHandler(
            JsonResponse("""{ "models": [{ "name": "qwen3:8b" }] }"""),
            JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "OK" },
                  "done_reason": "stop"
                }
                """));
        var checker = new OllamaConnectionChecker(handler);

        var result = await checker.CheckAsync(CreateSettings("qwen3:8b"), disableThinking: true);

        Assert.True(result.ChatContentReturned);
        Assert.False(result.ChatThinkingReturned);
        Assert.Equal("stop", result.ChatDoneReason);
    }

    [Fact]
    public async Task CheckAsync_WithThinkingOnly_AddsWarning()
    {
        var handler = new SequenceHttpMessageHandler(
            JsonResponse("""{ "models": [{ "name": "qwen3:8b" }] }"""),
            JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "", "thinking": "reasoning" },
                  "done_reason": "length"
                }
                """));
        var checker = new OllamaConnectionChecker(handler);

        var result = await checker.CheckAsync(CreateSettings("qwen3:8b"), disableThinking: true);

        Assert.True(result.ChatTestAttempted);
        Assert.False(result.ChatTestSuccess);
        Assert.True(result.ChatThinkingReturned);
        Assert.NotEmpty(result.ChatTestWarnings);
    }

    [Fact]
    public async Task CheckAsync_WithDoneReasonLength_AddsWarning()
    {
        var handler = new SequenceHttpMessageHandler(
            JsonResponse("""{ "models": [{ "name": "qwen3:8b" }] }"""),
            JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "partial" },
                  "done_reason": "length"
                }
                """));
        var checker = new OllamaConnectionChecker(handler);

        var result = await checker.CheckAsync(CreateSettings("qwen3:8b"), disableThinking: true);

        Assert.Contains(result.ChatTestWarnings, warning => warning.Contains("done_reason=length", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckAsync_WhenChatTestFails_DoesNotThrow()
    {
        var handler = new SequenceHttpMessageHandler(
            JsonResponse("""{ "models": [{ "name": "qwen3:8b" }] }"""),
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var checker = new OllamaConnectionChecker(handler);

        var result = await checker.CheckAsync(CreateSettings("qwen3:8b"), disableThinking: true);

        Assert.True(result.IsSuccess);
        Assert.True(result.ChatTestAttempted);
        Assert.False(result.ChatTestSuccess);
    }

    [Fact]
    public async Task CheckAsync_ChatTestUsesSmallFastGenerationOptions()
    {
        var handler = new SequenceHttpMessageHandler(
            JsonResponse("""{ "models": [{ "name": "qwen3-coder:30b" }] }"""),
            JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "OK" },
                  "done_reason": "stop"
                }
                """));
        var checker = new OllamaConnectionChecker(handler);

        _ = await checker.CheckAsync(CreateSettings("qwen3-coder:30b") with { TimeoutSeconds = 600 }, disableThinking: true);

        var chatRequestJson = Assert.Single(
            handler.RequestBodies,
            static body => body.Contains("\"messages\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(chatRequestJson);
        var root = document.RootElement;
        Assert.False(root.GetProperty("think").GetBoolean());
        var options = root.GetProperty("options");
        Assert.Equal(0.0, options.GetProperty("temperature").GetDouble());
        Assert.Equal(8, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(512, options.GetProperty("num_ctx").GetInt32());
    }

    private static LlmProviderSettings CreateSettings(string chatModel)
    {
        return new LlmProviderSettings
        {
            Endpoint = "http://localhost:11434",
            ChatModel = chatModel,
            TimeoutSeconds = 10,
        };
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new();
        public int RequestCount { get; private set; }

        public List<string> RequestBodies { get; } = [];

        public SequenceHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            foreach (var response in responses)
            {
                this.responses.Enqueue(response);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (responses.Count == 0)
            {
                throw new InvalidOperationException("No more queued responses.");
            }

            return SendAndCaptureAsync(request, cancellationToken);
        }

        private async Task<HttpResponseMessage> SendAndCaptureAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return responses.Dequeue();
        }
    }
}
