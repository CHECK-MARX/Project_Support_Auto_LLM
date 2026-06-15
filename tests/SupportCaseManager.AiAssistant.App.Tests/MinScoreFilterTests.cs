using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class MinScoreFilterTests
{
    [Fact]
    public void MinimumDisplayScore_HidesResultsBelowThreshold()
    {
        var items = new[]
        {
            CreateItem("above", 0.7),
            CreateItem("below", 0.69),
        };

        var visible = SearchSourceFiltering.Apply(items, SearchSourceFiltering.All, minimumDisplayScore: 0.7);

        Assert.Equal(["above"], visible.Select(static item => item.SourceId));
    }

    [Fact]
    public void MinimumDisplayScore_OneShowsOnlyScoreOneOrHigher()
    {
        var items = new[]
        {
            CreateItem("one", 1.0),
            CreateItem("below", 0.999),
        };

        var visible = SearchSourceFiltering.Apply(items, SearchSourceFiltering.All, minimumDisplayScore: 1.0);

        Assert.Equal(["one"], visible.Select(static item => item.SourceId));
    }

    [Fact]
    public void MinimumDisplayScore_InvalidHighValueIsClamped()
    {
        var items = new[]
        {
            CreateItem("below", 0.999),
        };

        var visible = SearchSourceFiltering.Apply(items, SearchSourceFiltering.All, minimumDisplayScore: 1000);

        Assert.Empty(visible);
    }

    [Fact]
    public void Summary_CountsVisibleAndHiddenByMinimumScore()
    {
        var items = new[]
        {
            CreateItem("visible", 0.8),
            CreateItem("hidden", 0.4),
        };

        var summary = SearchSourceSummaryBuilder.Build(
            items,
            SearchSourceFiltering.All,
            maxEvidenceItems: 8,
            autoSelectMinimumScore: 0.65,
            minimumDisplayScore: 0.7);

        Assert.Equal(1, summary.FilteredCount);
        Assert.Equal(1, summary.HiddenByMinimumScoreCount);
    }

    [Fact]
    public void AutoSelectThreshold_DoesNotUseDisplayMinimumScore()
    {
        var items = new[]
        {
            CreateItem("displayed-but-low", 0.5, isSelected: true),
            CreateItem("displayed-and-high", 0.8, isSelected: true),
        };

        var summary = SearchSourceSummaryBuilder.BuildAndApplyPlan(
            items,
            SearchSourceFiltering.All,
            maxEvidenceItems: 8,
            autoSelectMinimumScore: 0.65,
            minimumDisplayScore: 0.0);

        Assert.Equal(2, summary.FilteredCount);
        Assert.Equal(["displayed-and-high"], summary.Selection.Sources.Select(static source => source.SourceId));
        Assert.Equal("displayed-but-low", Assert.Single(summary.Selection.ExcludedByScoreSources).SourceId);
    }

    private static SearchSourceViewModel CreateItem(string id, double score, bool isSelected = true)
    {
        return new SearchSourceViewModel(
            new SearchSource
            {
                SourceId = id,
                SourceType = "Manual",
                Title = id,
                Text = $"Text {id}",
                Score = score,
            },
            isSelected);
    }
}
