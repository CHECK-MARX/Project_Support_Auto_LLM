using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Llm;

namespace SupportCaseManager.Ai.Tests.Llm;

public sealed class ModelRecommendationTests
{
    [Fact]
    public void BuildRecommendationText_ForQwen38B_PromptsThinkFalseChatTest()
    {
        var text = ModelRecommendationHelper.BuildRecommendationText("qwen3:8b");

        Assert.Contains("qwen3:8b", text);
        Assert.Contains("think:false", text);
        Assert.Contains("qwen3:8b 推奨初期設定", text);
    }

    [Fact]
    public void BuildRecommendationText_ForQwen314B_PromptsThinkFalseChatTest()
    {
        var text = ModelRecommendationHelper.BuildRecommendationText("qwen3:14b");

        Assert.Contains("qwen3:14b", text);
        Assert.Contains("think:false", text);
        Assert.Contains("qwen3:14b 推奨初期設定", text);
    }

    [Fact]
    public void BuildRecommendationText_ForQwen314B_DoesNotRequireAutoPull()
    {
        var text = ModelRecommendationHelper.BuildRecommendationText("qwen3:14b");

        Assert.DoesNotContain("ollama pull", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildQwen314BSettings_ContainsRecommendedValues()
    {
        var text = ModelRecommendationHelper.BuildQwen314BSettings();

        Assert.Contains("最大根拠件数: 2", text);
        Assert.Contains("自動選択最小スコア: 0.30", text);
        Assert.Contains("最大プロンプト文字数: 6000", text);
        Assert.Contains("最大出力トークン: 800", text);
        Assert.Contains("num_ctx: 8192", text);
        Assert.Contains("タイムアウト秒数: 600", text);
        Assert.Contains("Thinkingを無効化: ON", text);
        Assert.Contains("根拠0件時は生成しない: ON", text);
        Assert.Contains("TopN fallback: ON", text);
    }

    [Fact]
    public void BuildQwen38BSettings_ContainsRecommendedValues()
    {
        var text = ModelRecommendationHelper.BuildQwen38BSettings();

        Assert.Contains("最大根拠件数: 2", text);
        Assert.Contains("自動選択最小スコア: 0.30", text);
        Assert.Contains("最大プロンプト文字数: 6000", text);
        Assert.Contains("最大出力トークン: 800", text);
        Assert.Contains("num_ctx: 8192", text);
        Assert.Contains("Thinkingを無効化: ON", text);
        Assert.Contains("根拠0件時は生成しない: ON", text);
        Assert.Contains("TopN fallback: ON", text);
    }

    [Theory]
    [InlineData("qwen3:8b")]
    [InlineData("qwen3:14b")]
    public void OllamaRequestBody_ForQwen3Models_IncludesThinkFalse(string model)
    {
        var body = OllamaRequestBuilder.BuildChatRequestBody(
            new LlmProviderSettings { ChatModel = model, Temperature = 0.2, MaxOutputTokens = 100 },
            "system",
            "user",
            thinkDisabled: true);

        var json = System.Text.Json.JsonSerializer.Serialize(body);
        Assert.Contains("\"think\":false", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRecommendationText_IncludesComparisonWorkflow()
    {
        var text = ModelRecommendationHelper.BuildRecommendationText("qwen3:14b");

        Assert.Contains("OfficialDocが回答根拠に使われるか確認", text);
        Assert.Contains("qwen3:8b と qwen3:14b で回答品質を比較", text);
    }
}
