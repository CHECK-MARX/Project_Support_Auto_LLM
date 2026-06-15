using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public class SearchSourceFilteringTests
{
    [Fact]
    public void Apply_AllReturnsEverySource()
    {
        var items = CreateMixedItems();

        var visible = SearchSourceFiltering.Apply(items, SearchSourceFiltering.All);

        Assert.Equal(["case1", "manual1", "case2"], visible.Select(static item => item.SourceId));
    }

    [Fact]
    public void Apply_PastCaseNoteReturnsOnlyPastCaseNotes()
    {
        var items = CreateMixedItems();

        var visible = SearchSourceFiltering.Apply(items, SearchSourceFiltering.PastCaseNote);

        Assert.Equal(["case1", "case2"], visible.Select(static item => item.SourceId));
    }

    [Fact]
    public void Apply_ManualReturnsOnlyManuals()
    {
        var items = CreateMixedItems();

        var visible = SearchSourceFiltering.Apply(items, SearchSourceFiltering.Manual);

        var item = Assert.Single(visible);
        Assert.Equal("manual1", item.SourceId);
    }

    [Fact]
    public void Apply_MinimumDisplayScoreHidesLowerScores()
    {
        var items = CreateMixedItems();

        var visible = SearchSourceFiltering.Apply(items, SearchSourceFiltering.All, minimumDisplayScore: 0.7);

        Assert.Equal(["case1", "case2"], visible.Select(static item => item.SourceId));
    }

    [Fact]
    public void Apply_MinimumDisplayScoreAboveOneIsClampedToOne()
    {
        var items = CreateMixedItems();

        var visible = SearchSourceFiltering.Apply(items, SearchSourceFiltering.All, minimumDisplayScore: 1000);

        Assert.Empty(visible);
    }

    [Fact]
    public void Apply_DoesNotChangeSelectionStateForHiddenItems()
    {
        var items = CreateMixedItems();
        items[1].IsSelected = true;

        _ = SearchSourceFiltering.Apply(items, SearchSourceFiltering.PastCaseNote);

        Assert.True(items[1].IsSelected);
    }

    [Fact]
    public void SetVisibleSelection_SelectsOnlyVisibleItems()
    {
        var items = CreateMixedItems();
        items.ForEach(static item => item.IsSelected = false);

        SearchSourceFiltering.SetVisibleSelection(items, SearchSourceFiltering.Manual, isSelected: true);

        Assert.False(items[0].IsSelected);
        Assert.True(items[1].IsSelected);
        Assert.False(items[2].IsSelected);
    }

    [Fact]
    public void SetVisibleSelection_UsesMinimumDisplayScore()
    {
        var items = CreateMixedItems();
        items.ForEach(static item => item.IsSelected = false);

        SearchSourceFiltering.SetVisibleSelection(
            items,
            SearchSourceFiltering.All,
            isSelected: true,
            minimumDisplayScore: 0.7);

        Assert.True(items[0].IsSelected);
        Assert.False(items[1].IsSelected);
        Assert.True(items[2].IsSelected);
    }

    [Fact]
    public void SetVisibleSelection_ClearsOnlyVisibleItems()
    {
        var items = CreateMixedItems();
        items.ForEach(static item => item.IsSelected = true);

        SearchSourceFiltering.SetVisibleSelection(items, SearchSourceFiltering.PastCaseNote, isSelected: false);

        Assert.False(items[0].IsSelected);
        Assert.True(items[1].IsSelected);
        Assert.False(items[2].IsSelected);
    }

    [Fact]
    public void SelectHighScoreVisible_SelectsVisibleItemsAtOrAboveThreshold()
    {
        var items = CreateMixedItems();
        items.ForEach(static item => item.IsSelected = false);

        SearchSourceFiltering.SelectHighScoreVisible(items, SearchSourceFiltering.All, minimumScore: 0.7);

        Assert.True(items[0].IsSelected);
        Assert.False(items[1].IsSelected);
        Assert.True(items[2].IsSelected);
    }

    [Fact]
    public void ClearAll_ClearsAllSourceTypes()
    {
        var items = CreateMixedItems();
        items.ForEach(static item => item.IsSelected = true);

        SearchSourceFiltering.ClearAll(items);

        Assert.All(items, static item => Assert.False(item.IsSelected));
    }

    [Fact]
    public void Build_DeterminesSentAndExcludedSourcesAcrossSourceTypesByScore()
    {
        var items = new List<SearchSourceViewModel>
        {
            CreateItem("case-low", "PastCaseNote", 0.2),
            CreateItem("manual-high", "Manual", 0.9),
            CreateItem("case-mid", "PastCaseNote", 0.7),
        };
        items.ForEach(static item => item.IsSelected = true);

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 2);

        Assert.Equal(["manual-high", "case-mid"], result.Sources.Select(static source => source.SourceId));
        Assert.Empty(result.ExcludedSelectedSources);
        Assert.Equal("case-low", Assert.Single(result.ExcludedByScoreSources).SourceId);
    }

    private static List<SearchSourceViewModel> CreateMixedItems()
    {
        return
        [
            CreateItem("case1", "PastCaseNote", 0.9),
            CreateItem("manual1", "Manual", 0.5),
            CreateItem("case2", "PastCaseNote", 0.7),
        ];
    }

    private static SearchSourceViewModel CreateItem(string id, string sourceType, double score)
    {
        return new SearchSourceViewModel(
            new SearchSource
            {
                SourceId = id,
                SourceType = sourceType,
                Title = id,
                Text = $"Text for {id}",
                FilePath = $@"D:\Sources\{id}.txt",
                SupportNumber = sourceType == "PastCaseNote" ? "00001234" : null,
                Score = score,
            },
            isSelected: true);
    }
}
