using System.Net;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;

namespace SupportCaseManager.Ai.Tests.Llm;

public sealed class ModelResolutionTests
{
    [Fact]
    public void Resolve_RestoresAvailableSavedModel()
    {
        var result = OllamaModelResolver.Resolve(
            "qwen3:4b",
            AnswerQualityModes.Standard,
            ["gemma4:26b", "qwen3:4b"]);

        Assert.Equal("qwen3:4b", result.Model);
        Assert.Equal(ModelResolutionSources.Saved, result.Source);
    }

    [Fact]
    public void Resolve_WhenModelIsEmpty_UsesQualityPreset()
    {
        var result = OllamaModelResolver.Resolve(
            null,
            AnswerQualityModes.Standard,
            ["qwen3:8b", "gemma4:26b"]);

        Assert.Equal("gemma4:26b", result.Model);
        Assert.Equal(ModelResolutionSources.Preset, result.Source);
    }

    [Fact]
    public void Resolve_WhenStandardPresetIsMissing_FallsBackToGemma31b()
    {
        var result = OllamaModelResolver.Resolve(
            string.Empty,
            AnswerQualityModes.Standard,
            ["qwen3:8b", "gemma4:31b", "qwen3:4b"]);

        Assert.Equal("gemma4:31b", result.Model);
        Assert.Equal(ModelResolutionSources.Fallback, result.Source);
    }

    [Fact]
    public async Task OllamaClient_WhenModelIsEmpty_DoesNotSendHttpRequest()
    {
        var handler = new CountingHandler();
        var client = new OllamaClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(
            new PromptMessages { SystemPrompt = "system", UserPrompt = "user" },
            new LlmProviderSettings { ChatModel = " " }));

        Assert.Contains("未設定", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.SendCount);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
