using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class EvidenceSelectionTests
{
    [Fact]
    public void SearchResults_AreNotAllSentAfterSearchWhenScoresAreLow()
    {
        var items = new[]
        {
            CreateItem("high", "Manual", 0.9, isSelected: true),
            CreateItem("low", "PastCaseNote", 0.2, isSelected: true),
        };

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 8, autoSelectMinimumScore: 0.65);

        Assert.Equal(["high"], result.Sources.Select(static source => source.SourceId));
        Assert.Equal("low", Assert.Single(result.ExcludedByScoreSources).SourceId);
    }

    [Fact]
    public void AutoSelectThreshold_SendsOnlyThresholdOrHigher()
    {
        var items = new[]
        {
            CreateItem("above", "Manual", 0.65, isSelected: true),
            CreateItem("below", "Manual", 0.649, isSelected: true),
        };

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 8, autoSelectMinimumScore: 0.65);

        Assert.Equal(["above"], result.Sources.Select(static source => source.SourceId));
    }

    [Fact]
    public void MaxEvidenceItems_LimitsByScoreOrder()
    {
        var items = new[]
        {
            CreateItem("third", "Manual", 0.7, isSelected: true),
            CreateItem("first", "Manual", 0.9, isSelected: true),
            CreateItem("second", "PastCaseNote", 0.8, isSelected: true),
        };

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 2, autoSelectMinimumScore: 0.65);

        Assert.Equal(["first", "second"], result.Sources.Select(static source => source.SourceId));
        Assert.Equal("third", Assert.Single(result.ExcludedSelectedSources).SourceId);
    }

    [Fact]
    public void ManualSelection_IsPrioritizedOverScoreThreshold()
    {
        var item = CreateItem("manual-low", "Manual", 0.2, isSelected: false);
        item.IsSelected = true;

        var result = SearchSourceSelectionBuilder.Build([item], maxEvidenceItems: 8, autoSelectMinimumScore: 0.65);

        Assert.Equal("manual-low", Assert.Single(result.Sources).SourceId);
        Assert.Empty(result.ExcludedByScoreSources);
    }

    [Fact]
    public void SendStatus_ShowsExclusionReason()
    {
        var item = CreateItem("low", "Manual", 0.2, isSelected: true);

        _ = SearchSourceSummaryBuilder.BuildAndApplyPlan(
            [item],
            SearchSourceFiltering.All,
            maxEvidenceItems: 8,
            autoSelectMinimumScore: 0.65);

        Assert.Equal("Excluded by score", item.SendStatusText);
        Assert.Contains("score", item.SelectionReasonText, StringComparison.OrdinalIgnoreCase);
    }

    private static SearchSourceViewModel CreateItem(string id, string sourceType, double score, bool isSelected)
    {
        return new SearchSourceViewModel(
            new SearchSource
            {
                SourceId = id,
                SourceType = sourceType,
                Title = id,
                Text = $"Text {id}",
                Score = score,
            },
            isSelected);
    }
}
