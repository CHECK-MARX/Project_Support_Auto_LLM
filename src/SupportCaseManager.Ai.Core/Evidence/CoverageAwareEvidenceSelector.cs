using System.Globalization;

namespace SupportCaseManager.Ai.Core.Evidence;

public static class CoverageAwareEvidenceSelector
{
    private const double NearDuplicateThreshold = 0.88;

    public static CoverageEvidenceSelectionResult Select(CoverageEvidenceSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var required = Normalize(request.RequiredCoverage);
        var candidates = request.Candidates
            .Where(static item => item is not null && !string.IsNullOrWhiteSpace(item.CandidateId))
            .OrderBy(static item => item.OriginalRank)
            .ThenBy(static item => item.CandidateId, StringComparer.Ordinal)
            .ToList();
        var baseMax = Math.Clamp(request.BaseMaxItems, 1, 5);
        var expansionMax = Math.Clamp(Math.Max(baseMax, request.ExpansionMaxItems), baseMax, 5);
        var budget = Math.Max(0, request.CharacterBudget);
        var minimumQuality = Math.Clamp(request.MinimumQualityScore, 0, 1);
        var selected = new List<CoverageEvidenceCandidate>();
        var decisions = new List<CoverageEvidenceSelectionDecision>();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var selectedCoverage = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var usedChars = 0;
        var redundantSkippedIds = new HashSet<string>(StringComparer.Ordinal);
        var budgetLimited = false;

        foreach (var manual in candidates.Where(static item => item.IsManuallySelected))
        {
            if (!selectedIds.Add(manual.CandidateId))
            {
                continue;
            }

            selected.Add(manual);
            var addedCoverage = Normalize(manual.Coverage)
                .Where(item => required.Contains(item, StringComparer.Ordinal) && !selectedCoverage.Contains(item))
                .ToList();
            selectedCoverage.UnionWith(Normalize(manual.Coverage));
            usedChars += EstimatedChars(manual);
            decisions.Add(new CoverageEvidenceSelectionDecision
            {
                CandidateId = manual.CandidateId,
                QualityScore = QualityScore(manual),
                SetScore = QualityScore(manual),
                AddedCoverage = addedCoverage,
                IsManual = true,
                Reason = "ManualSelection",
            });
        }

        if (selected.Count > expansionMax)
        {
            warnings.Add("ManualSelectionExceedsLimit");
        }
        if (usedChars > budget && selected.Count > 0)
        {
            warnings.Add("ManualSelectionExceedsCharacterBudget");
        }

        var eligible = candidates
            .Where(item => !selectedIds.Contains(item.CandidateId))
            .Where(static item => !item.ExplicitlyExcluded && !item.TopicConflict && !item.ProductMismatch)
            .Where(item => QualityScore(item) >= minimumQuality)
            .ToList();

        while (eligible.Count > 0 && selected.Count < expansionMax)
        {
            var coverageComplete = required.Count == 0 || required.All(selectedCoverage.Contains);
            if (selected.Count >= baseMax && coverageComplete)
            {
                break;
            }

            var hasAnchor = selected.Count > 0;
            var ranked = eligible
                .Select(item => AssessCandidate(item, selected, selectedCoverage, required))
                .OrderByDescending(item => hasAnchor ? item.NewRequiredCoverageCount : 0)
                .ThenByDescending(item => hasAnchor && selected.All(selectedItem =>
                    !string.Equals(
                        selectedItem.SourceType,
                        item.Candidate.SourceType,
                        StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(static item => item.SetScore)
                .ThenByDescending(static item => item.QualityScore)
                .ThenBy(static item => item.Candidate.OriginalRank)
                .ThenBy(static item => item.Candidate.CandidateId, StringComparer.Ordinal)
                .ToList();
            CandidateAssessment? chosen = null;
            foreach (var assessment in ranked)
            {
                if (selected.Count >= baseMax && assessment.NewRequiredCoverageCount == 0)
                {
                    continue;
                }
                if (IsRedundant(assessment.Candidate, selected, assessment.NewRequiredCoverageCount > 0))
                {
                    redundantSkippedIds.Add(assessment.Candidate.CandidateId);
                    continue;
                }

                var candidateChars = EstimatedChars(assessment.Candidate);
                if (selected.Count > 0 && usedChars + candidateChars > budget)
                {
                    if (assessment.NewRequiredCoverageCount > 0)
                    {
                        budgetLimited = true;
                    }
                    continue;
                }

                chosen = assessment;
                break;
            }

            if (chosen is null)
            {
                break;
            }

            selected.Add(chosen.Candidate);
            selectedIds.Add(chosen.Candidate.CandidateId);
            var addedCoverage = Normalize(chosen.Candidate.Coverage)
                .Where(item => required.Contains(item, StringComparer.Ordinal) && !selectedCoverage.Contains(item))
                .ToList();
            selectedCoverage.UnionWith(Normalize(chosen.Candidate.Coverage));
            usedChars += EstimatedChars(chosen.Candidate);
            eligible.Remove(chosen.Candidate);
            decisions.Add(new CoverageEvidenceSelectionDecision
            {
                CandidateId = chosen.Candidate.CandidateId,
                QualityScore = chosen.QualityScore,
                SetScore = chosen.SetScore,
                AddedCoverage = addedCoverage,
                Reason = selected.Count == 1 ? "QualityAnchor" : "CoverageCompletion",
            });
        }

        var searchCoverage = Normalize(candidates.SelectMany(static item => item.Coverage));
        var missing = required.Where(item => !selectedCoverage.Contains(item)).ToList();
        var statuses = BuildStatuses(request.CorpusCoverage, required, searchCoverage, missing, budgetLimited,
            selected.Count >= expansionMax);
        return new CoverageEvidenceSelectionResult
        {
            Selected = selected,
            Decisions = decisions,
            RequiredCoverage = required,
            SearchCoverage = searchCoverage,
            SelectedCoverage = selectedCoverage.Order(StringComparer.Ordinal).ToList(),
            MissingCoverage = missing,
            Statuses = statuses,
            Warnings = warnings,
            RedundantCandidatesSkipped = redundantSkippedIds.Count,
            BudgetLimited = budgetLimited,
            EstimatedChars = usedChars,
        };
    }

    private static CandidateAssessment AssessCandidate(
        CoverageEvidenceCandidate candidate,
        IReadOnlyList<CoverageEvidenceCandidate> selected,
        IReadOnlySet<string> selectedCoverage,
        IReadOnlyList<string> required)
    {
        var candidateCoverage = Normalize(candidate.Coverage);
        var newRequired = candidateCoverage.Count(item => required.Contains(item, StringComparer.Ordinal) && !selectedCoverage.Contains(item));
        var quality = QualityScore(candidate);
        var sourceDiversity = selected.Count == 0 || selected.All(item => !string.Equals(item.SourceType, candidate.SourceType, StringComparison.OrdinalIgnoreCase)) ? 1.0 : 0.0;
        var technicalDiversity = selected.Count == 0 ? 1.0 : 1.0 - selected.Max(item => Jaccard(item.TechnicalTokens, candidate.TechnicalTokens));
        var coverageValue = selected.Count == 0 ? 0 : newRequired;
        var setScore = coverageValue + (0.35 * quality) + (0.05 * sourceDiversity) + (0.05 * technicalDiversity);
        return new CandidateAssessment(candidate, newRequired, quality, setScore);
    }

    private static double QualityScore(CoverageEvidenceCandidate item) => Math.Clamp(
        (0.45 * Clamp(item.RankingScore)) +
        (0.20 * Clamp(item.TopicScore)) +
        (0.10 * Clamp(item.EntityScore)) +
        (0.10 * Clamp(item.TechnicalTokenScore)) +
        (0.10 * Clamp(item.SourceTrust)) +
        (0.05 * Clamp(item.VersionScore)) +
        item.ConflictPenalty,
        0,
        1);

    private static bool IsRedundant(
        CoverageEvidenceCandidate candidate,
        IReadOnlyList<CoverageEvidenceCandidate> selected,
        bool addsRequiredCoverage)
    {
        foreach (var existing in selected)
        {
            if (!string.IsNullOrWhiteSpace(candidate.ContentHash) &&
                string.Equals(candidate.ContentHash, existing.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var sameDocument = Same(candidate.DocumentId, existing.DocumentId) || Same(candidate.FilePath, existing.FilePath);
            var sameSection = Same(candidate.Section, existing.Section);
            if (sameDocument && (sameSection || !addsRequiredCoverage))
            {
                return true;
            }
            if (SameDocumentFamily(candidate.DocumentTitle, existing.DocumentTitle) &&
                HasAnalysisProcedureSignature(candidate.Text) &&
                HasAnalysisProcedureSignature(existing.Text))
            {
                return true;
            }
            if (HasEnoughTextForSimilarity(candidate.Text, existing.Text) &&
                TextSimilarity(candidate.Text, existing.Text) >= NearDuplicateThreshold)
            {
                return true;
            }
            // Shared commands identify the operation, not duplicate evidence. Two different
            // sources that both mention `qacli analyze` may cover setup and verification
            // separately, so content/document checks above decide duplication.
        }
        return false;
    }

    private static IReadOnlyList<string> BuildStatuses(
        IReadOnlyList<string>? corpusCoverage,
        IReadOnlyList<string> required,
        IReadOnlyList<string> searchCoverage,
        IReadOnlyList<string> missing,
        bool budgetLimited,
        bool itemLimitReached)
    {
        var statuses = new List<string>();
        if (corpusCoverage is not null && required.Except(Normalize(corpusCoverage), StringComparer.Ordinal).Any())
        {
            statuses.Add("MissingCoverageInCorpus");
        }
        if (required.Except(searchCoverage, StringComparer.Ordinal).Any())
        {
            statuses.Add("MissingCoverageInSearchResults");
        }
        if (missing.Count == 0)
        {
            statuses.Add("CoverageSatisfied");
        }
        else if ((budgetLimited || itemLimitReached) && missing.Any(searchCoverage.Contains))
        {
            statuses.Add("SelectionBudgetExceeded");
        }
        else
        {
            statuses.Add("CoverageInsufficient");
        }
        return statuses;
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> values) => values
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Select(static value => value.Trim())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();

    private static int EstimatedChars(CoverageEvidenceCandidate item) => item.EstimatedChars > 0
        ? item.EstimatedChars
        : item.Text.Length;

    private static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static double TextSimilarity(string left, string right) => Jaccard(Terms(left), Terms(right));

    private static bool HasEnoughTextForSimilarity(string left, string right) =>
        left.Length >= 80 && right.Length >= 80 && Terms(left).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 8 &&
        Terms(right).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 8;

    private static bool SameDocumentFamily(string? left, string? right)
    {
        static string NormalizeTitle(string? value) => new(
            (value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

        var normalizedLeft = NormalizeTitle(left);
        return normalizedLeft.Length >= 8 && string.Equals(normalizedLeft, NormalizeTitle(right), StringComparison.Ordinal);
    }

    private static bool HasAnalysisProcedureSignature(string value) =>
        (value.Contains("qacli analyze", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("qaclianalyze", StringComparison.OrdinalIgnoreCase)) &&
        (value.Contains("]>[") || value.Contains("Analyze Project", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("Run Analysis", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> Terms(string value) => value
        .Split([' ', '\r', '\n', '\t', ',', '.', ':', ';', '/', '\\', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static term => term.Length > 1)
        .Select(static term => term.ToUpper(CultureInfo.InvariantCulture))
        .Take(400)
        .ToList();

    private static double Jaccard(IEnumerable<string> left, IEnumerable<string> right)
    {
        var leftSet = left.Where(static item => !string.IsNullOrWhiteSpace(item)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightSet = right.Where(static item => !string.IsNullOrWhiteSpace(item)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (leftSet.Count == 0 || rightSet.Count == 0)
        {
            return 0;
        }
        return leftSet.Intersect(rightSet, StringComparer.OrdinalIgnoreCase).Count() /
            (double)leftSet.Union(rightSet, StringComparer.OrdinalIgnoreCase).Count();
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);

    private sealed record CandidateAssessment(
        CoverageEvidenceCandidate Candidate,
        int NewRequiredCoverageCount,
        double QualityScore,
        double SetScore);
}
