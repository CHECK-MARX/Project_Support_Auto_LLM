namespace SupportCaseManager.AiAssistant.App.ViewModels;

public static class SearchSourceSummaryBuilder
{
    public const double DefaultAutoSelectMinimumScore = 0.30;

    public static SearchSourceSummary BuildAndApplyPlan(
        IEnumerable<SearchSourceViewModel> searchResults,
        string? sourceTypeFilter,
        int maxEvidenceItems,
        double autoSelectMinimumScore = DefaultAutoSelectMinimumScore,
        double minimumDisplayScore = 0,
        bool isFreshnessSensitive = false,
        bool enableTopNFallback = false)
    {
        if (searchResults is null)
        {
            throw new ArgumentNullException(nameof(searchResults));
        }

        var allItems = searchResults.ToList();
        var summary = Build(allItems, sourceTypeFilter, maxEvidenceItems, autoSelectMinimumScore, minimumDisplayScore, isFreshnessSensitive, enableTopNFallback);
        ApplyPlannedState(allItems, summary.Selection);
        return summary;
    }

    public static SearchSourceSummary Build(
        IEnumerable<SearchSourceViewModel> searchResults,
        string? sourceTypeFilter,
        int maxEvidenceItems,
        double autoSelectMinimumScore = DefaultAutoSelectMinimumScore,
        double minimumDisplayScore = 0,
        bool isFreshnessSensitive = false,
        bool enableTopNFallback = false)
    {
        if (searchResults is null)
        {
            throw new ArgumentNullException(nameof(searchResults));
        }

        var allItems = searchResults.ToList();
        var displayScore = Math.Clamp(minimumDisplayScore, 0.0, 1.0);
        var autoScore = Math.Clamp(autoSelectMinimumScore, 0.0, 1.0);
        var filteredItems = SearchSourceFiltering.Apply(allItems, sourceTypeFilter, displayScore);
        var sourceTypeFilteredItems = SearchSourceFiltering.Apply(allItems, sourceTypeFilter);
        var selection = SearchSourceSelectionBuilder.Build(allItems, maxEvidenceItems, autoScore, isFreshnessSensitive, enableTopNFallback);

        return new SearchSourceSummary
        {
            Selection = selection,
            FilteredCount = filteredItems.Count,
            HiddenBySourceTypeFilterCount = Math.Max(0, allItems.Count - sourceTypeFilteredItems.Count),
            HiddenByMinimumScoreCount = Math.Max(0, sourceTypeFilteredItems.Count - filteredItems.Count),
            BelowAutoSelectScoreCount = allItems.Count(item => (item.Score ?? 0) < autoScore),
            AutoSelectMinimumScore = autoScore,
            MinimumDisplayScore = displayScore,
        };
    }

    private static void ApplyPlannedState(
        IEnumerable<SearchSourceViewModel> searchResults,
        SearchSourceSelectionResult selection)
    {
        var sendIds = selection.Sources
            .Select(static source => source.SourceId ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        var excludedIds = selection.ExcludedSelectedSources
            .Select(static source => source.SourceId ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        var scoreExcludedIds = selection.ExcludedByScoreSources
            .Select(static source => source.SourceId ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in searchResults)
        {
            item.WillBeSentToLlm = sendIds.Contains(item.SourceId);
            item.IsExcludedByLimit = excludedIds.Contains(item.SourceId);
            item.IsExcludedByScore = !sendIds.Contains(item.SourceId) &&
                (scoreExcludedIds.Contains(item.SourceId)
                    || (!item.IsSelected && (item.Score ?? 0) < selection.AutoSelectMinimumScore));
        }
    }
}

public sealed record class SearchSourceSummary
{
    public SearchSourceSelectionResult Selection { get; init; } = new();

    public int FilteredCount { get; init; }

    public int HiddenBySourceTypeFilterCount { get; init; }

    public int HiddenByMinimumScoreCount { get; init; }

    public int BelowAutoSelectScoreCount { get; init; }

    public double AutoSelectMinimumScore { get; init; } = SearchSourceSummaryBuilder.DefaultAutoSelectMinimumScore;

    public double MinimumDisplayScore { get; init; }
}
