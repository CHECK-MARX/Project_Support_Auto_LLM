namespace SupportCaseManager.AiAssistant.App.ViewModels;

public static class SearchSourceFiltering
{
    public const string All = "All";
    public const string PastCaseNote = "PastCaseNote";
    public const string Manual = "Manual";
    public const string OfficialDoc = "OfficialDoc";

    public static IReadOnlyList<SearchSourceViewModel> Apply(
        IEnumerable<SearchSourceViewModel> searchResults,
        string? sourceTypeFilter,
        double minimumDisplayScore = 0)
    {
        if (searchResults is null)
        {
            throw new ArgumentNullException(nameof(searchResults));
        }

        return searchResults
            .Where(item => Matches(item, sourceTypeFilter))
            .Where(item => (item.Score ?? 0) >= Math.Clamp(minimumDisplayScore, 0.0, 1.0))
            .ToList();
    }

    public static void SetVisibleSelection(
        IEnumerable<SearchSourceViewModel> searchResults,
        string? sourceTypeFilter,
        bool isSelected,
        double minimumDisplayScore = 0)
    {
        foreach (var item in Apply(searchResults, sourceTypeFilter, minimumDisplayScore))
        {
            item.IsSelected = isSelected;
        }
    }

    public static void SelectHighScoreVisible(
        IEnumerable<SearchSourceViewModel> searchResults,
        string? sourceTypeFilter,
        double minimumScore,
        double minimumDisplayScore = 0)
    {
        var threshold = Math.Clamp(minimumScore, 0.0, 1.0);
        foreach (var item in Apply(searchResults, sourceTypeFilter, minimumDisplayScore))
        {
            item.IsSelected = (item.Score ?? 0) >= threshold;
        }
    }

    public static void ClearAll(IEnumerable<SearchSourceViewModel> searchResults)
    {
        if (searchResults is null)
        {
            throw new ArgumentNullException(nameof(searchResults));
        }

        foreach (var item in searchResults)
        {
            item.IsSelected = false;
        }
    }

    public static bool Matches(SearchSourceViewModel item, string? sourceTypeFilter)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (string.IsNullOrWhiteSpace(sourceTypeFilter)
            || string.Equals(sourceTypeFilter, All, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(item.SourceType, sourceTypeFilter, StringComparison.OrdinalIgnoreCase);
    }
}
