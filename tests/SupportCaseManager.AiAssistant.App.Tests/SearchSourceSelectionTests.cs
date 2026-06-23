using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public class SearchSourceSelectionTests
{
    [Fact]
    public void Build_IncludesOnlySelectedSources()
    {
        var items = new[]
        {
            CreateViewModel("s1", score: 0.9, isSelected: true),
            CreateViewModel("s2", score: 0.8, isSelected: false),
            CreateViewModel("s3", score: 0.7, isSelected: true),
        };

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 5);

        Assert.Equal(["s1", "s3"], result.Sources.Select(static source => source.SourceId));
        Assert.Equal(3, result.SearchResultCount);
        Assert.Equal(2, result.SelectedCount);
    }

    [Fact]
    public void Build_ExcludesUnselectedSources()
    {
        var items = new[]
        {
            CreateViewModel("selected", score: 0.8, isSelected: true),
            CreateViewModel("unselected", score: 1.0, isSelected: false),
        };

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 5);

        Assert.Single(result.Sources);
        Assert.Equal("selected", result.Sources[0].SourceId);
    }

    [Fact]
    public void Build_LimitsByMaxEvidenceItemsInScoreOrder()
    {
        var items = new[]
        {
            CreateViewModel("low", score: 0.7, isSelected: true),
            CreateViewModel("high", score: 0.9, isSelected: true),
            CreateViewModel("middle", score: 0.8, isSelected: true),
        };

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 2);

        Assert.Equal(["high", "middle"], result.Sources.Select(static source => source.SourceId));
        Assert.True(result.WasLimited);
        Assert.Single(result.ExcludedSelectedSources);
        Assert.Equal("low", result.ExcludedSelectedSources[0].SourceId);
        Assert.Contains("最大件数", result.Warning);
    }

    [Fact]
    public void Build_AllowsZeroEvidenceAndReturnsWarning()
    {
        var items = new[]
        {
            CreateViewModel("s1", score: 0.9, isSelected: false),
        };

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 5);

        Assert.Empty(result.Sources);
        Assert.Equal(0, result.SelectedCount);
        Assert.Contains("根拠が0件", result.Warning);
    }

    [Fact]
    public void SearchSourceDto_DoesNotHaveUiSelectionState()
    {
        var propertyNames = typeof(SearchSource)
            .GetProperties()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("IsSelected", propertyNames);
        Assert.DoesNotContain("WasUsedInLastDraft", propertyNames);
    }

    [Fact]
    public void Build_CalculatesSelectedCountsBySourceType()
    {
        var items = new[]
        {
            CreateViewModel("case1", score: 0.9, isSelected: true, sourceType: "PastCaseNote"),
            CreateViewModel("case2", score: 0.8, isSelected: true, sourceType: "PastCaseNote"),
            CreateViewModel("manual1", score: 0.7, isSelected: true, sourceType: "Manual"),
            CreateViewModel("manual2", score: 0.6, isSelected: false, sourceType: "Manual"),
        };

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 8);

        Assert.Equal(2, result.PastCaseNoteSelectedCount);
        Assert.Equal(1, result.ManualSelectedCount);
    }

    [Fact]
    public void Build_IncludesSelectedManualSourceInDraftSources()
    {
        var items = new[]
        {
            CreateViewModel("manual-license", score: 0.9, isSelected: true, sourceType: "Manual"),
            CreateViewModel("case-hidden", score: 0.8, isSelected: false, sourceType: "PastCaseNote"),
        };

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 8);

        var source = Assert.Single(result.Sources);
        Assert.Equal("manual-license", source.SourceId);
        Assert.Equal("Manual", source.SourceType);
        Assert.Equal(1, result.ManualSelectedCount);
    }

    [Fact]
    public void Build_ExcludesSelectedLowScoreSourcesByDefault()
    {
        var items = new[]
        {
            CreateViewModel("high", score: 0.8, isSelected: true),
            CreateViewModel("low", score: 0.2, isSelected: true),
        };

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 5, autoSelectMinimumScore: 0.65);

        Assert.Equal(["high"], result.Sources.Select(static source => source.SourceId));
        var excluded = Assert.Single(result.ExcludedByScoreSources);
        Assert.Equal("low", excluded.SourceId);
        Assert.Equal(1, result.ExcludedByScoreCount);
        Assert.Contains("自動選択最小スコア", result.Warning);
    }

    [Fact]
    public void Build_ManualSelectionOverridesLowScoreThreshold()
    {
        var item = CreateViewModel("manual-low", score: 0.2, isSelected: false, sourceType: "Manual");
        item.IsSelected = true;

        var result = SearchSourceSelectionBuilder.Build([item], maxEvidenceItems: 5, autoSelectMinimumScore: 0.65);

        var source = Assert.Single(result.Sources);
        Assert.Equal("manual-low", source.SourceId);
        Assert.Empty(result.ExcludedByScoreSources);
    }

    [Fact]
    public void Build_MixesManualAndPastCaseAndExcludesLowAutomaticScores()
    {
        var items = new[]
        {
            CreateViewModel("manual-low", score: 0.2, isSelected: true, sourceType: "Manual"),
            CreateViewModel("case-high", score: 0.9, isSelected: true, sourceType: "PastCaseNote"),
            CreateViewModel("manual-high", score: 0.8, isSelected: true, sourceType: "Manual"),
        };

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 5, autoSelectMinimumScore: 0.65);

        Assert.Equal(["case-high", "manual-high"], result.Sources.Select(static source => source.SourceId));
        Assert.Equal("manual-low", Assert.Single(result.ExcludedByScoreSources).SourceId);
    }

    [Fact]
    public void Build_WithTopNFallbackUsesTopSourcesWhenNormalSelectionIsEmpty()
    {
        var items = new[]
        {
            CreateViewModel("low-1", score: 0.20, isSelected: false, sourceType: "Manual"),
            CreateViewModel("low-2", score: 0.10, isSelected: false, sourceType: "PastCaseNote"),
            CreateViewModel("low-3", score: 0.05, isSelected: false, sourceType: "Manual"),
        };

        var result = SearchSourceSelectionBuilder.Build(
            items,
            maxEvidenceItems: 2,
            autoSelectMinimumScore: 0.65,
            enableTopNFallback: true);

        Assert.Equal(["low-1", "low-2"], result.Sources.Select(static source => source.SourceId));
        Assert.True(result.TopNFallbackApplied);
        Assert.Equal(0, result.SelectedCount);
        Assert.Equal(1, result.ManualSendCount);
        Assert.Equal(1, result.PastCaseNoteSendCount);
        Assert.Contains("TopN fallback", result.Warning);
    }

    [Fact]
    public void ViewModel_CanKeepLastDraftUsageStateForDisplay()
    {
        var item = CreateViewModel("s1", score: 0.9, isSelected: true);

        item.WasUsedInLastDraft = true;

        Assert.True(item.WasUsedInLastDraft);
        Assert.Equal("Yes", item.UsedInLastDraftText);
    }

    private static SearchSourceViewModel CreateViewModel(
        string sourceId,
        double score,
        bool isSelected,
        string sourceType = "PastCaseNote")
    {
        return new SearchSourceViewModel(
            new SearchSource
            {
                SourceId = sourceId,
                SourceType = sourceType,
                Title = $"Title {sourceId}",
                Text = $"Excerpt {sourceId}",
                FilePath = $@"D:\Cases\{sourceId}.txt",
                SupportNumber = "00001234",
                Score = score,
            },
            isSelected);
    }
}
