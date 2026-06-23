using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class SearchSourceSummaryBuilderTests
{
    [Fact]
    public void BuildAndApplyPlan_ManualSourceUpdatesSummaryAndSendPlan()
    {
        var items = new[]
        {
            CreateViewModel("manual-license", "Manual", 1.0, isSelected: true),
        };

        var summary = SearchSourceSummaryBuilder.BuildAndApplyPlan(
            items,
            SearchSourceFiltering.All,
            maxEvidenceItems: 8);

        Assert.Equal(1, summary.Selection.SearchResultCount);
        Assert.Equal(1, summary.FilteredCount);
        Assert.Equal(1, summary.Selection.SelectedCount);
        Assert.Equal(1, summary.Selection.ManualSelectedCount);
        Assert.Single(summary.Selection.Sources);
        Assert.True(items[0].WillBeSentToLlm);
        Assert.False(items[0].IsExcludedByLimit);
        Assert.Equal("Will send", items[0].SendStatusText);
    }

    [Fact]
    public void BuildAndApplyPlan_AllFilterIncludesManualInSummary()
    {
        var items = new[]
        {
            CreateViewModel("manual-license", "Manual", 1.0, isSelected: true),
        };

        var summary = SearchSourceSummaryBuilder.BuildAndApplyPlan(
            items,
            SearchSourceFiltering.All,
            maxEvidenceItems: 8);

        Assert.Equal(1, summary.FilteredCount);
        Assert.Equal(0, summary.HiddenBySourceTypeFilterCount);
        Assert.Equal(1, summary.Selection.ManualSelectedCount);
    }

    [Fact]
    public void BuildAndApplyPlan_ManualFilterIncludesManualInSummary()
    {
        var items = new[]
        {
            CreateViewModel("manual-license", "Manual", 1.0, isSelected: true),
        };

        var summary = SearchSourceSummaryBuilder.BuildAndApplyPlan(
            items,
            SearchSourceFiltering.Manual,
            maxEvidenceItems: 8);

        Assert.Equal(1, summary.FilteredCount);
        Assert.Equal(0, summary.HiddenBySourceTypeFilterCount);
        Assert.Equal(1, summary.Selection.ManualSelectedCount);
        Assert.Single(summary.Selection.Sources);
    }

    [Fact]
    public void BuildAndApplyPlan_PastCaseFilterHidesManualButKeepsSelectionAndSources()
    {
        var items = new[]
        {
            CreateViewModel("manual-license", "Manual", 1.0, isSelected: true),
        };

        var summary = SearchSourceSummaryBuilder.BuildAndApplyPlan(
            items,
            SearchSourceFiltering.PastCaseNote,
            maxEvidenceItems: 8);

        Assert.Equal(0, summary.FilteredCount);
        Assert.Equal(1, summary.HiddenBySourceTypeFilterCount);
        Assert.Equal(1, summary.Selection.SelectedCount);
        Assert.Equal(1, summary.Selection.ManualSelectedCount);
        Assert.Single(summary.Selection.Sources);
        Assert.True(items[0].IsSelected);
        Assert.True(items[0].WillBeSentToLlm);
    }

    [Fact]
    public void BuildAndApplyPlan_DoesNotThrowForManualSource()
    {
        var items = new[]
        {
            CreateViewModel("manual-license", "Manual", 1.0, isSelected: true),
        };

        var exception = Record.Exception(() => SearchSourceSummaryBuilder.BuildAndApplyPlan(
            items,
            SearchSourceFiltering.All,
            maxEvidenceItems: 8));

        Assert.Null(exception);
    }

    [Fact]
    public void BuildAndApplyPlan_CountsHiddenByMinimumDisplayScore()
    {
        var items = new[]
        {
            CreateViewModel("high", "Manual", 0.9, isSelected: true),
            CreateViewModel("low", "Manual", 0.4, isSelected: false),
        };

        var summary = SearchSourceSummaryBuilder.BuildAndApplyPlan(
            items,
            SearchSourceFiltering.All,
            maxEvidenceItems: 8,
            autoSelectMinimumScore: 0.65,
            minimumDisplayScore: 0.7);

        Assert.Equal(2, summary.Selection.SearchResultCount);
        Assert.Equal(1, summary.FilteredCount);
        Assert.Equal(1, summary.HiddenByMinimumScoreCount);
        Assert.Equal(0.7, summary.MinimumDisplayScore);
    }

    [Fact]
    public void BuildAndApplyPlan_SeparatesDisplayMinimumFromAutoSelectThreshold()
    {
        var items = new[]
        {
            CreateViewModel("visible-low", "Manual", 0.5, isSelected: true),
            CreateViewModel("visible-high", "Manual", 0.8, isSelected: true),
        };

        var summary = SearchSourceSummaryBuilder.BuildAndApplyPlan(
            items,
            SearchSourceFiltering.All,
            maxEvidenceItems: 8,
            autoSelectMinimumScore: 0.65,
            minimumDisplayScore: 0.0);

        Assert.Equal(2, summary.FilteredCount);
        Assert.Equal(["visible-high"], summary.Selection.Sources.Select(static source => source.SourceId));
        Assert.Equal("visible-low", Assert.Single(summary.Selection.ExcludedByScoreSources).SourceId);
    }

    [Fact]
    public void BuildAndApplyPlan_WithTopNFallbackMarksFallbackSourcesAsWillSend()
    {
        var items = new[]
        {
            CreateViewModel("low-1", "Manual", 0.20, isSelected: false),
            CreateViewModel("low-2", "PastCaseNote", 0.10, isSelected: false),
            CreateViewModel("low-3", "Manual", 0.05, isSelected: false),
        };

        var summary = SearchSourceSummaryBuilder.BuildAndApplyPlan(
            items,
            SearchSourceFiltering.All,
            maxEvidenceItems: 2,
            autoSelectMinimumScore: 0.65,
            enableTopNFallback: true);

        Assert.True(summary.Selection.TopNFallbackApplied);
        Assert.Equal(["low-1", "low-2"], summary.Selection.Sources.Select(static source => source.SourceId));
        Assert.Equal(0, summary.Selection.SelectedCount);
        Assert.Equal(1, summary.Selection.ManualSendCount);
        Assert.Equal(1, summary.Selection.PastCaseNoteSendCount);
        Assert.True(items[0].WillBeSentToLlm);
        Assert.True(items[1].WillBeSentToLlm);
        Assert.False(items[2].WillBeSentToLlm);
        Assert.Equal("Will send by fallback", items[0].SendStatusText);
    }

    private static SearchSourceViewModel CreateViewModel(
        string sourceId,
        string sourceType,
        double score,
        bool isSelected)
    {
        return new SearchSourceViewModel(
            new SearchSource
            {
                SourceId = sourceId,
                SourceType = sourceType,
                Title = "license_error_manual - ライセンス認証エラー対応手順",
                Text = "ライセンス認証エラー、ライセンスサーバー名、ポート番号、ファイアウォール設定を確認します。",
                FilePath = @"D:\Manuals\license_error_manual.md",
                Score = score,
            },
            isSelected);
    }
}
