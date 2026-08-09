using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public static class CoverageAwareSearchSourceSelector
{
    public static SearchSourceSelectionResult Select(
        IReadOnlyList<SearchSourceViewModel> items,
        int baseMaxItems,
        double minimumScore,
        QuestionAwareEvidenceSelectionContext context)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(context);

        var selectedItems = items.Where(static item => item.IsSelected).ToList();
        if (baseMaxItems <= 0)
        {
            return BuildResult(items, selectedItems, [], new CoverageEvidenceSelectionResult
            {
                RequiredCoverage = [],
                SearchCoverage = [],
                SelectedCoverage = [],
                MissingCoverage = [],
                Statuses = ["CoverageInsufficient"],
            }, 0, minimumScore, []);
        }

        var catalog = SupportTopicCatalog.Create(context.ProductName);
        var topicAnalysis = NegationAwareTopicAnalyzer.Analyze(context.InquiryText, catalog);
        var queryProfile = topicAnalysis.PrimaryProfile ?? TopicEntityAnalyzer.Extract(context.InquiryText, catalog);
        var technicalTokens = QuestionAwareEvidenceRanker.ExtractExactTechnicalTokens(context.InquiryText);
        var rankingCandidates = items.Select((item, index) => new TopicEntityRankingCandidate
        {
            CandidateIndex = index,
            CandidateId = CandidateId(item, index),
            Text = DocumentText(item),
            SourceType = item.SourceType,
            ProductName = item.ProductName,
            BaseSearchScore = item.Score ?? 0,
            OriginalRank = index + 1,
            IsManuallySelected = item.IsManuallySelected,
            Profile = TopicEntityAnalyzer.Extract(DocumentText(item), catalog),
        }).ToList();
        var ranked = TopicEntityRanker.Rank(new TopicEntityRankingRequest
        {
            QueryProfile = queryProfile,
            ExcludedProfile = topicAnalysis.ExcludedProfile,
            TechnicalTokens = technicalTokens,
            RequestedProduct = context.ProductName,
            RequestedVersion = context.TargetVersion,
            Candidates = rankingCandidates,
            MaxItems = Math.Clamp(context.CoverageAwareMaxEvidenceItems, 1, 5),
        });
        var requiredCoverage = CoverageAnalyzer.RequiredForCoverageSelection(context.InquiryText, queryProfile);
        var assessments = ranked.Assessed.ToDictionary(static item => item.CandidateIndex);
        var candidates = items.Select((item, index) =>
        {
            var assessment = assessments[index];
            return new CoverageEvidenceCandidate
            {
                CandidateId = CandidateId(item, index),
                OriginalRank = index + 1,
                SourceType = item.SourceType,
                DocumentId = item.Source.DocumentId ?? item.FilePath ?? item.Url ?? item.SupportNumber,
                FilePath = item.FilePath,
                Section = item.Source.SectionTitle ?? item.Title,
                Text = DocumentText(item),
                ContentHash = item.Source.ContentHash ?? assessment.TextFingerprint,
                TechnicalTokens = assessment.ExactTechnicalTokens,
                Coverage = CoverageAnalyzer.ObserveForCoverageSelection(DocumentText(item)).ToList(),
                RankingScore = assessment.FinalScore,
                TopicScore = assessment.TopicScore,
                EntityScore = assessment.EntityScore,
                TechnicalTokenScore = assessment.TechnicalTokenScore,
                SourceTrust = assessment.SourceTrustScore,
                VersionScore = assessment.VersionScore,
                ConflictPenalty = assessment.ConflictPenalty,
                ExplicitlyExcluded = item.IsManuallyExcluded || assessment.ExplicitlyExcluded,
                TopicConflict = assessment.TopicConflict,
                ProductMismatch = assessment.ProductMatch is false,
                IsManuallySelected = item.IsManuallySelected,
                EstimatedChars = item.Title.Length + item.Text.Length,
            };
        }).ToList();

        var selection = CoverageAwareEvidenceSelector.Select(new CoverageEvidenceSelectionRequest
        {
            RequiredCoverage = requiredCoverage,
            Candidates = candidates,
            BaseMaxItems = Math.Clamp(baseMaxItems, 1, 5),
            ExpansionMaxItems = Math.Clamp(context.CoverageAwareMaxEvidenceItems, 1, 5),
            CharacterBudget = Math.Max(600, context.MaxPromptChars / 2),
            MinimumQualityScore = Math.Clamp(minimumScore, 0, 1),
        });
        var selectedIds = selection.Selected.Select(static item => item.CandidateId).ToHashSet(StringComparer.Ordinal);
        var selectedViewModels = items
            .Select((item, index) => (Item: item, Id: CandidateId(item, index)))
            .Where(item => selectedIds.Contains(item.Id))
            .OrderBy(item => selection.Selected.FindIndex(candidate => candidate.CandidateId == item.Id))
            .Select(static item => item.Item)
            .ToList();

        return BuildResult(
            items,
            selectedItems,
            selectedViewModels,
            selection,
            Math.Clamp(baseMaxItems, 1, 5),
            minimumScore,
            new QuestionClassifier().Classify(context.InquiryText).QuestionTypes);
    }

    private static SearchSourceSelectionResult BuildResult(
        IReadOnlyList<SearchSourceViewModel> allItems,
        IReadOnlyList<SearchSourceViewModel> selectedItems,
        IReadOnlyList<SearchSourceViewModel> selected,
        CoverageEvidenceSelectionResult selection,
        int baseMaxItems,
        double minimumScore,
        IReadOnlyList<string> questionTypes)
    {
        var sources = selected.Select(static item => item.Source).ToList();
        var selectedIds = selected.ToHashSet();
        var excludedSelected = selectedItems
            .Where(item => !selectedIds.Contains(item))
            .Select(static item => item.Source)
            .ToList();
        var excludedByScore = selectedItems
            .Where(item => !item.IsManuallySelected && !selectedIds.Contains(item) && (item.Score ?? 0) < minimumScore)
            .Select(static item => item.Source)
            .ToList();
        var warningParts = selection.Warnings
            .Concat(selection.Statuses.Where(static status => status != "CoverageSatisfied"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new SearchSourceSelectionResult
        {
            Sources = sources,
            ExcludedSelectedSources = excludedSelected,
            ExcludedByScoreSources = excludedByScore,
            SearchResultCount = allItems.Count,
            SelectedCount = selectedItems.Count,
            PastCaseNoteSelectedCount = selectedItems.Count(static item => IsSourceType(item.SourceType, "PastCaseNote")),
            ManualSelectedCount = selectedItems.Count(static item => IsSourceType(item.SourceType, "Manual")),
            OfficialDocSelectedCount = selectedItems.Count(static item => IsSourceType(item.SourceType, "OfficialDoc")),
            PastCaseNoteSendCount = sources.Count(static item => IsSourceType(item.SourceType, "PastCaseNote")),
            ManualSendCount = sources.Count(static item => IsSourceType(item.SourceType, "Manual")),
            OfficialDocSendCount = sources.Count(static item => IsSourceType(item.SourceType, "OfficialDoc")),
            MaxEvidenceItems = Math.Clamp(Math.Max(baseMaxItems, selected.Count), 0, 5),
            AutoSelectMinimumScore = Math.Clamp(minimumScore, 0, 1),
            WasLimited = excludedSelected.Count > 0 || selection.BudgetLimited,
            TopNFallbackApplied = selected.Any(static item => !item.IsSelected),
            QuestionAwareSelectionApplied = true,
            QuestionTypes = questionTypes,
            FinalCoverage = selection.SelectedCoverage,
            RequiredCoverage = selection.RequiredCoverage,
            SearchCoverage = selection.SearchCoverage,
            MissingCoverage = selection.MissingCoverage,
            InsufficientEvidenceReasons = selection.Statuses
                .Where(static status => status != "CoverageSatisfied")
                .ToList(),
            RankingMode = "Phase18CoverageAware",
            SelectionMode = selection.SelectionMode,
            RedundantCandidatesSkipped = selection.RedundantCandidatesSkipped,
            SelectionBudgetLimited = selection.BudgetLimited,
            EstimatedEvidenceChars = selection.EstimatedChars,
            Warning = string.Join("; ", warningParts),
        };
    }

    private static string CandidateId(SearchSourceViewModel item, int index) =>
        $"{(string.IsNullOrWhiteSpace(item.SourceId) ? "candidate" : item.SourceId)}#{index}";

    private static string DocumentText(SearchSourceViewModel item) => string.Join(
        '\n',
        item.Title,
        item.Source.QuestionText,
        item.Source.InternalMemo,
        item.Text);

    private static bool IsSourceType(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static int FindIndex(
        this IReadOnlyList<CoverageEvidenceCandidate> items,
        Predicate<CoverageEvidenceCandidate> match)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (match(items[index]))
            {
                return index;
            }
        }

        return int.MaxValue;
    }
}
