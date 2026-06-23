using System.Net;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;

namespace SupportCaseManager.Ai.Tests.Llm;

public sealed class OllamaClientThinkingTests
{
    [Fact]
    public async Task GenerateAsync_WithDisableThinkingTrue_IncludesThinkFalseInRequestJson()
    {
        string? requestJson = null;
        var client = new OllamaClient(new StubHttpMessageHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "{}" },
                  "done": true,
                  "done_reason": "stop"
                }
                """);
        }));

        _ = await client.GenerateAsync(CreateMessages(), CreateSettings(chatModel: "llama3.1"), disableThinking: true);

        Assert.NotNull(requestJson);
        using var document = JsonDocument.Parse(requestJson);
        Assert.False(document.RootElement.GetProperty("think").GetBoolean());
    }

    [Theory]
    [InlineData("qwen3:8b")]
    [InlineData("qwen3:14b")]
    public async Task GenerateAsync_WithQwen3Models_IncludesThinkFalse(string model)
    {
        string? requestJson = null;
        var client = new OllamaClient(new StubHttpMessageHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "OK" },
                  "done_reason": "stop"
                }
                """);
        }));

        _ = await client.GenerateAsync(CreateMessages(), CreateSettings(chatModel: model), disableThinking: true);

        Assert.NotNull(requestJson);
        using var document = JsonDocument.Parse(requestJson);
        Assert.False(document.RootElement.GetProperty("think").GetBoolean());
    }

    [Fact]
    public async Task GenerateAsync_WithContentOk_ReturnsNormalResult()
    {
        var client = new OllamaClient(new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "message": { "role": "assistant", "content": "OK" },
              "done_reason": "stop",
              "total_duration": 1800000000,
              "eval_count": 2,
              "prompt_eval_count": 10
            }
            """)));

        var result = await client.GenerateAsync(CreateMessages(), CreateSettings(chatModel: "qwen3:8b"));

        Assert.Equal("OK", result.Content);
        Assert.True(result.ContentReturned);
        Assert.False(result.ThinkingReturned);
        Assert.Equal("stop", result.DoneReason);
    }

    [Fact]
    public async Task GenerateAsync_WithThinkingOnly_ThrowsSpecificDiagnosticMessage()
    {
        var client = new OllamaClient(new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "message": {
                "role": "assistant",
                "content": "",
                "thinking": "internal reasoning"
              },
              "done_reason": "length"
            }
            """)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateAsync(CreateMessages(), CreateSettings(chatModel: "qwen3:8b")));

        Assert.Contains("thinking出力のみ", exception.Message);
    }

    [Fact]
    public async Task GenerateAsync_WithThinkingOnlyFirstResponse_RetriesOnceWithStrongNoThinkInstruction()
    {
        var requestJsons = new List<string>();
        var client = new OllamaClient(new StubHttpMessageHandler(async request =>
        {
            requestJsons.Add(await request.Content!.ReadAsStringAsync());
            return requestJsons.Count == 1
                ? JsonResponse("""
                    {
                      "message": {
                        "role": "assistant",
                        "content": "",
                        "thinking": "internal reasoning"
                      },
                      "done_reason": "stop"
                    }
                    """)
                : JsonResponse("""
                    {
                      "message": {
                        "role": "assistant",
                        "content": "{\"customerReplyDraft\":\"回答\"}"
                      },
                      "done_reason": "stop"
                    }
                    """);
        }));

        var result = await client.GenerateAsync(
            CreateMessages(),
            CreateSettings(chatModel: "gpt-oss:20b"),
            disableThinking: true);

        Assert.Equal("{\"customerReplyDraft\":\"回答\"}", result.Content);
        Assert.Equal(2, requestJsons.Count);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("リトライ", StringComparison.Ordinal));

        using var retryDocument = JsonDocument.Parse(requestJsons[1]);
        var retryMessages = retryDocument.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Contains("message.content", retryMessages[0].GetProperty("content").GetString());
        Assert.Contains("message.content", retryMessages[1].GetProperty("content").GetString());
        Assert.False(retryDocument.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal(800, retryDocument.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public async Task GenerateAsync_WithDoneReasonLength_AddsWarningDiagnostic()
    {
        var client = new OllamaClient(new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "message": { "role": "assistant", "content": "{\"customerReplyDraft\":\"partial\"}" },
              "done_reason": "length"
            }
            """)));

        var result = await client.GenerateAsync(CreateMessages(), CreateSettings(chatModel: "qwen3:8b"));

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Contains("最大出力トークン", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_DoesNotRelyOnNoThinkPrefixAlone()
    {
        string? requestJson = null;
        var client = new OllamaClient(new StubHttpMessageHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {
                  "message": { "role": "assistant", "content": "OK" },
                  "done_reason": "stop"
                }
                """);
        }));

        _ = await client.GenerateAsync(
            new PromptMessages { SystemPrompt = "system", UserPrompt = "Reply with exactly this text: OK" },
            CreateSettings(chatModel: "qwen3:8b"),
            disableThinking: true);

        Assert.NotNull(requestJson);
        using var document = JsonDocument.Parse(requestJson);
        Assert.True(document.RootElement.TryGetProperty("think", out var thinkProperty));
        Assert.False(thinkProperty.GetBoolean());
    }

    private static PromptMessages CreateMessages()
    {
        return new PromptMessages
        {
            SystemPrompt = "system prompt",
            UserPrompt = "user prompt",
        };
    }

    private static LlmProviderSettings CreateSettings(string chatModel = "qwen3:8b")
    {
        return new LlmProviderSettings
        {
            Endpoint = "http://localhost:11434",
            ChatModel = chatModel,
            Temperature = 0.2,
            MaxOutputTokens = 100,
            TimeoutSeconds = 120,
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
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
        {
            sendAsync = request => Task.FromResult(send(request));
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync)
        {
            this.sendAsync = sendAsync;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return sendAsync(request);
        }
    }
}
