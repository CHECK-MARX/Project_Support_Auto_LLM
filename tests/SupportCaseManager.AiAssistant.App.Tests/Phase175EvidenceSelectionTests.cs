using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase175EvidenceSelectionTests
{
    [Fact]
    public void Ranking_ExcludedHighScoreDoesNotBeatPrimaryTopic()
    {
        var result = SearchSourceSelectionBuilder.Build(
            [
                Create("license", "Validate License Serverの設定方法です。", 1.0),
                Create("stream", "Validate Streamの概要、用途、作成、QACとの関連付け、設定、確認方法です。", 0.30),
                Create("ide", "Validate IDE Pluginの設定方法です。", 0.95),
            ],
            1,
            enableTopNFallback: true,
            questionAwareContext: Context());

        Assert.Single(result.Sources);
        Assert.Equal("stream", result.Sources[0].SourceId);
    }

    [Fact]
    public void Fallback_AvoidsExcludedTopicButRetainsManualSelection()
    {
        var license = Create("license", "Validate License Serverの設定方法です。", 1.0);
        var stream = Create("stream", "Validate Streamの概要、作成、QACとの関連付け、設定後の確認方法です。", 0.20);
        var automatic = SearchSourceSelectionBuilder.Build(
            [license, stream], 1, enableTopNFallback: true, questionAwareContext: Context());
        Assert.Equal("stream", automatic.Sources[0].SourceId);

        license.IsSelected = true;
        var manual = SearchSourceSelectionBuilder.Build(
            [license, stream], 1, enableTopNFallback: true, questionAwareContext: Context());
        Assert.Equal("license", manual.Sources[0].SourceId);
    }

    private static QuestionAwareEvidenceSelectionContext Context() => new()
    {
        Enabled = true,
        InquiryText = "License ServerやIDE PluginではなくValidate Streamについて教えてください。",
        ProductName = "HelixQAC",
        RankingMode = EvidenceRankingModes.Phase16,
        UsePhase175QualityControls = true,
    };

    private static SearchSourceViewModel Create(string id, string text, double score) => new(
        new SearchSource
        {
            SourceId = id,
            SourceType = "Manual",
            Title = id,
            Text = text,
            Score = score,
            ProductName = "HelixQAC",
        },
        isSelected: false);
}
