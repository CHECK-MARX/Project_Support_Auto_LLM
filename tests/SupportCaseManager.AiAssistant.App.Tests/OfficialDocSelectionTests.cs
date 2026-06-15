using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class OfficialDocSelectionTests
{
    [Fact]
    public void SelectionBuilder_CountsOfficialDocSelectedSources()
    {
        var items = new[]
        {
            CreateItem("official-1", "OfficialDoc", 0.9, isSelected: true),
            CreateItem("manual-1", "Manual", 0.8, isSelected: true),
            CreateItem("case-1", "PastCaseNote", 0.7, isSelected: false),
        };

        var result = SearchSourceSelectionBuilder.Build(items, maxEvidenceItems: 8, autoSelectMinimumScore: 0.65);

        Assert.Equal(2, result.SelectedCount);
        Assert.Equal(1, result.OfficialDocSelectedCount);
        Assert.Contains(result.Sources, source => source.SourceType == "OfficialDoc");
    }

    [Fact]
    public void SelectionBuilder_ForFreshnessSensitiveQuery_PrioritizesOfficialDocOverPastCase()
    {
        var items = new[]
        {
            CreateItem("case-1", "PastCaseNote", 0.95, isSelected: true),
            CreateItem("official-1", "OfficialDoc", 0.55, isSelected: true),
        };

        var result = SearchSourceSelectionBuilder.Build(
            items,
            maxEvidenceItems: 2,
            autoSelectMinimumScore: 0.65,
            isFreshnessSensitive: true);

        Assert.Equal(2, result.Sources.Count);
        Assert.Equal("OfficialDoc", result.Sources[0].SourceType);
    }

    [Fact]
    public void SelectionBuilder_ForFreshnessSensitiveWithoutOfficialDoc_LimitsManualAndPastCaseSources()
    {
        var items = new[]
        {
            CreateItem("manual-1", "Manual", 0.95, isSelected: true),
            CreateItem("manual-2", "Manual", 0.90, isSelected: true),
            CreateItem("manual-3", "Manual", 0.85, isSelected: true),
            CreateItem("case-1", "PastCaseNote", 0.99, isSelected: true),
            CreateItem("case-2", "PastCaseNote", 0.98, isSelected: true),
        };

        var result = SearchSourceSelectionBuilder.Build(
            items,
            maxEvidenceItems: 8,
            autoSelectMinimumScore: 0.65,
            isFreshnessSensitive: true);

        Assert.True(result.FreshnessNoOfficialDocLimitApplied);
        Assert.Equal(3, result.Sources.Count);
        Assert.Equal(2, result.Sources.Count(static source => source.SourceType == "Manual"));
        Assert.Equal(1, result.Sources.Count(static source => source.SourceType == "PastCaseNote"));
        Assert.Contains("OfficialDoc", result.Warning);
        Assert.Equal(2, result.ExcludedSelectedCount);
    }

    [Fact]
    public void SourceTypeFilter_OfficialDoc_ReturnsOnlyOfficialDocs()
    {
        var items = new[]
        {
            CreateItem("official-1", "OfficialDoc", 0.9, isSelected: true),
            CreateItem("manual-1", "Manual", 0.8, isSelected: true),
        };

        var filtered = SearchSourceFiltering.Apply(items, SearchSourceFiltering.OfficialDoc);

        var item = Assert.Single(filtered);
        Assert.Equal("OfficialDoc", item.SourceType);
    }

    private static SearchSourceViewModel CreateItem(string id, string sourceType, double score, bool isSelected)
    {
        return new SearchSourceViewModel(
            new SearchSource
            {
                SourceId = id,
                SourceType = sourceType,
                Title = id,
                Text = "text",
                Score = score,
                Url = sourceType == "OfficialDoc" ? "https://docs.example.test" : null,
            },
            isSelected);
    }
}
