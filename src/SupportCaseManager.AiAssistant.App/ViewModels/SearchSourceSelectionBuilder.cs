using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Search;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public static class SearchSourceSelectionBuilder
{
    private const int FreshnessNoOfficialDocManualLimit = 2;
    private const int FreshnessNoOfficialDocPastCaseLimit = 1;

    public static SearchSourceSelectionResult Build(
        IEnumerable<SearchSourceViewModel> searchResults,
        int maxEvidenceItems,
        double autoSelectMinimumScore = SearchSourceSummaryBuilder.DefaultAutoSelectMinimumScore,
        bool isFreshnessSensitive = false,
        bool enableTopNFallback = false)
    {
        if (searchResults is null)
        {
            throw new ArgumentNullException(nameof(searchResults));
        }

        var allItems = searchResults
            .Where(static item => item is not null)
            .ToList();
        var maxItems = Math.Max(0, maxEvidenceItems);
        var threshold = Math.Clamp(autoSelectMinimumScore, 0.0, 1.0);
        var selectedItems = allItems
            .Where(static item => item.IsSelected)
            .ToList();
        var selectedCandidates = selectedItems
            .Where(item => item.IsManuallySelected || MeetsAutoSelectThreshold(item, threshold, isFreshnessSensitive))
            .ToList();
        var excludedByScore = selectedItems
            .Except(selectedCandidates)
            .Select(static item => item.Source)
            .ToList();
        var topNFallbackApplied = false;

        if (selectedCandidates.Count == 0 && enableTopNFallback && maxItems > 0)
        {
            var fallbackCandidates = allItems
                .Where(static item => !item.IsManuallyExcluded);
            selectedCandidates = isFreshnessSensitive
                ? fallbackCandidates
                    .OrderBy(item => FreshnessEvidenceAutoSelector.GetSourcePriority(item.SourceType, isFreshnessSensitive))
                    .ThenByDescending(static item => item.Score ?? 0)
                    .ThenBy(static item => item.SourceId ?? string.Empty, StringComparer.Ordinal)
                    .Take(maxItems)
                    .ToList()
                : fallbackCandidates
                    .OrderByDescending(static item => item.Score ?? 0)
                    .ThenBy(static item => item.SourceId ?? string.Empty, StringComparer.Ordinal)
                    .Take(maxItems)
                    .ToList();
            topNFallbackApplied = selectedCandidates.Count > 0;
        }

        List<SearchSourceViewModel> orderedSelectedItems = isFreshnessSensitive
            ? selectedCandidates
                .OrderBy(item => FreshnessEvidenceAutoSelector.GetSourcePriority(item.SourceType, isFreshnessSensitive))
                .ThenByDescending(static item => item.Score ?? 0)
                .ThenBy(static item => item.SourceId ?? string.Empty, StringComparer.Ordinal)
                .ToList()
            : selectedCandidates
                .OrderByDescending(static item => item.Score ?? 0)
                .ThenBy(static item => item.SourceId ?? string.Empty, StringComparer.Ordinal)
                .ToList();

        var freshnessNoOfficialDocLimitApplied = false;
        var freshnessLimitedItems = new List<SearchSourceViewModel>();
        if (isFreshnessSensitive &&
            orderedSelectedItems.Count > 0 &&
            !orderedSelectedItems.Any(static item => IsSourceType(item, "OfficialDoc")))
        {
            freshnessNoOfficialDocLimitApplied = true;
            var allowed = orderedSelectedItems
                .Where(static item => IsSourceType(item, "Manual"))
                .Take(FreshnessNoOfficialDocManualLimit)
                .Concat(orderedSelectedItems
                    .Where(static item => IsSourceType(item, "PastCaseNote"))
                    .Take(FreshnessNoOfficialDocPastCaseLimit))
                .ToHashSet();

            freshnessLimitedItems = orderedSelectedItems
                .Where(item => !allowed.Contains(item))
                .ToList();
            orderedSelectedItems = orderedSelectedItems
                .Where(allowed.Contains)
                .ToList();
        }

        var sources = orderedSelectedItems
            .Take(maxItems)
            .Select(static item => item.Source)
            .ToList();
        var excludedSources = orderedSelectedItems
            .Skip(maxItems)
            .Concat(freshnessLimitedItems)
            .Select(static item => item.Source)
            .ToList();

        var wasLimited = selectedCandidates.Count > sources.Count;
        return new SearchSourceSelectionResult
        {
            Sources = sources,
            ExcludedSelectedSources = excludedSources,
            ExcludedByScoreSources = excludedByScore,
            SearchResultCount = allItems.Count,
            SelectedCount = selectedItems.Count,
            PastCaseNoteSelectedCount = selectedItems.Count(static item => IsSourceType(item, "PastCaseNote")),
            ManualSelectedCount = selectedItems.Count(static item => IsSourceType(item, "Manual")),
            OfficialDocSelectedCount = selectedItems.Count(static item => IsSourceType(item, "OfficialDoc")),
            PastCaseNoteSendCount = sources.Count(static source => IsSourceType(source, "PastCaseNote")),
            ManualSendCount = sources.Count(static source => IsSourceType(source, "Manual")),
            OfficialDocSendCount = sources.Count(static source => IsSourceType(source, "OfficialDoc")),
            MaxEvidenceItems = maxItems,
            AutoSelectMinimumScore = threshold,
            WasLimited = wasLimited,
            FreshnessNoOfficialDocLimitApplied = freshnessNoOfficialDocLimitApplied,
            TopNFallbackApplied = topNFallbackApplied,
            Warning = BuildWarning(
                selectedItems.Count,
                sources.Count,
                maxItems,
                wasLimited,
                excludedByScore.Count,
                threshold,
                freshnessNoOfficialDocLimitApplied,
                topNFallbackApplied),
        };
    }

    private static bool MeetsAutoSelectThreshold(
        SearchSourceViewModel item,
        double threshold,
        bool isFreshnessSensitive)
    {
        return FreshnessEvidenceAutoSelector.ShouldAutoSelect(item.Source, isFreshnessSensitive, threshold);
    }

    private static string BuildWarning(
        int selectedCount,
        int usedCount,
        int maxEvidenceItems,
        bool wasLimited,
        int excludedByScoreCount,
        double autoSelectMinimumScore,
        bool freshnessNoOfficialDocLimitApplied,
        bool topNFallbackApplied)
    {
        if (usedCount == 0)
        {
            return "LLMへ送信予定の根拠が0件です。根拠なしでも生成できますが、回答内容の確認が必要です。";
        }

        if (topNFallbackApplied)
        {
            return $"通常選択の根拠が0件のため、TopN fallbackでスコア上位{usedCount}件をLLMへ送信します。";
        }

        if (freshnessNoOfficialDocLimitApplied)
        {
            return "鮮度重要な問い合わせですがOfficialDoc根拠がありません。Manualは最大2件、PastCaseNoteは最大1件に制限し、最新情報として断定しないでください。";
        }

        if (wasLimited)
        {
            return $"選択中の根拠が最大件数を超えています。スコア上位{maxEvidenceItems}件だけをLLMへ送信します。";
        }

        if (excludedByScoreCount > 0)
        {
            return $"選択中の根拠のうち{excludedByScoreCount}件は自動選択最小スコア {autoSelectMinimumScore:0.000} 未満のためLLMへ送信しません。";
        }

        return string.Empty;
    }

    private static bool IsSourceType(SearchSourceViewModel item, string sourceType)
    {
        return string.Equals(item.SourceType, sourceType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceType(SearchSource source, string sourceType)
    {
        return string.Equals(source.SourceType, sourceType, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record class SearchSourceSelectionResult
{
    public IReadOnlyList<SearchSource> Sources { get; init; } = [];

    public IReadOnlyList<SearchSource> ExcludedSelectedSources { get; init; } = [];

    public IReadOnlyList<SearchSource> ExcludedByScoreSources { get; init; } = [];

    public int SearchResultCount { get; init; }

    public int SelectedCount { get; init; }

    public int PastCaseNoteSelectedCount { get; init; }

    public int ManualSelectedCount { get; init; }

    public int OfficialDocSelectedCount { get; init; }

    public int PastCaseNoteSendCount { get; init; }

    public int ManualSendCount { get; init; }

    public int OfficialDocSendCount { get; init; }

    public int ExcludedSelectedCount => ExcludedSelectedSources.Count;

    public int ExcludedByScoreCount => ExcludedByScoreSources.Count;

    public int MaxEvidenceItems { get; init; }

    public double AutoSelectMinimumScore { get; init; }

    public bool WasLimited { get; init; }

    public bool FreshnessNoOfficialDocLimitApplied { get; init; }

    public bool TopNFallbackApplied { get; init; }

    public string Warning { get; init; } = string.Empty;
}
