using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public static class CoverageAwareSearchSourceSelector
{
    private const double CoverageSelectorMinimumQualityCeiling = 0.20;
    private const int CoverageExcerptMaxLength = 240;

    public static bool ShouldApplyAutomatically(string? inquiryText, string? productName)
    {
        if (string.IsNullOrWhiteSpace(inquiryText))
        {
            return false;
        }

        var catalog = SupportTopicCatalog.Create(productName);
        var analysis = NegationAwareTopicAnalyzer.Analyze(inquiryText, catalog);
        var profile = analysis.PrimaryProfile ?? TopicEntityAnalyzer.Extract(inquiryText, catalog);
        var analysisHowTo = profile.Operations.Contains("Analysis", StringComparer.Ordinal) &&
            profile.Intents.Contains("HowTo", StringComparer.Ordinal);
        if (!profile.Features.Contains("Stream", StringComparer.OrdinalIgnoreCase) && !analysisHowTo)
        {
            return false;
        }

        var required = CoverageAnalyzer.RequiredForCoverageSelection(inquiryText, profile);
        if (analysisHowTo)
        {
            return required.Contains(CoverageAnalyzer.AnalysisProcedure, StringComparer.Ordinal) &&
                required.Contains(CoverageAnalyzer.AnalysisCommand, StringComparer.Ordinal);
        }

        return required.Contains(CoverageAnalyzer.Overview, StringComparer.Ordinal) &&
            required.Contains(CoverageAnalyzer.Configuration, StringComparer.Ordinal);
    }

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
        var compoundFeatureQuestion = ShouldApplyAutomatically(context.InquiryText, context.ProductName);
        var analysisHowTo = queryProfile.Operations.Contains("Analysis", StringComparer.Ordinal) &&
            queryProfile.Intents.Contains("HowTo", StringComparer.Ordinal);
        var compoundStreamQuestion = compoundFeatureQuestion &&
            queryProfile.Features.Contains("Stream", StringComparer.OrdinalIgnoreCase);
        var requiredCoverage = CoverageAnalyzer.RequiredForCoverageSelection(context.InquiryText, queryProfile).ToList();
        var assessments = ranked.Assessed.ToDictionary(static item => item.CandidateIndex);
        var nonPastCoverage = items
            .Select((item, index) => (Item: item, Assessment: assessments[index]))
            .Where(item => item.Assessment.HasTopicMatch && !IsPastEvidenceSourceType(item.Item.SourceType))
            .SelectMany(item => CoverageAnalyzer.ObserveForCoverageSelection(DocumentText(item.Item)))
            .ToHashSet(StringComparer.Ordinal);
        var nonPastSourcesCoverRequestedContent = requiredCoverage.Count > 0 &&
            requiredCoverage.All(nonPastCoverage.Contains);
        var streamRoleAssignments = compoundStreamQuestion
            ? AssignStreamCoverageRoles(items, assessments)
            : (OverviewCandidateIndex: (int?)null, ConfigurationCandidateIndex: (int?)null);
        if (compoundStreamQuestion &&
            baseMaxItems >= 3 &&
            items.Select((item, index) => (Item: item, Assessment: assessments[index]))
                .Any(item => item.Assessment.HasTopicMatch && IsPastEvidenceSourceType(item.Item.SourceType)))
        {
            requiredCoverage.Add(CoverageAnalyzer.PriorCaseSupplement);
        }
        requiredCoverage = requiredCoverage.Distinct(StringComparer.Ordinal).ToList();
        var candidates = items.Select((item, index) =>
        {
            var assessment = assessments[index];
            var analysisAdjustment = AnalysisSelectionAdjustment(queryProfile, item);
            var coverage = CoverageAnalyzer.ObserveForCoverageSelection(DocumentText(item)).ToList();
            if (compoundStreamQuestion &&
                assessment.HasTopicMatch &&
                !IsPastEvidenceSourceType(item.SourceType))
            {
                coverage.RemoveAll(static value =>
                    value is CoverageAnalyzer.Overview or
                        CoverageAnalyzer.Purpose or
                        CoverageAnalyzer.Configuration);
                if (index == streamRoleAssignments.OverviewCandidateIndex)
                {
                    coverage.Add(CoverageAnalyzer.Overview);
                    coverage.Add(CoverageAnalyzer.Purpose);
                }

                if (index == streamRoleAssignments.ConfigurationCandidateIndex)
                {
                    coverage.Add(CoverageAnalyzer.Configuration);
                }
            }

            if (compoundStreamQuestion &&
                assessment.HasTopicMatch &&
                IsPastEvidenceSourceType(item.SourceType))
            {
                if (nonPastSourcesCoverRequestedContent)
                {
                    coverage.RemoveAll(requiredCoverage.Contains);
                }
                coverage.Add(CoverageAnalyzer.PriorCaseSupplement);
            }
            return new CoverageEvidenceCandidate
            {
                CandidateId = CandidateId(item, index),
                OriginalRank = index + 1,
                SourceType = item.SourceType,
                DocumentTitle = item.Source.DocumentTitle ?? item.Title,
                DocumentId = item.Source.DocumentId ?? item.FilePath ?? item.Url ?? item.SupportNumber,
                FilePath = item.FilePath,
                Section = item.Source.SectionTitle ?? item.Title,
                Text = DocumentText(item),
                ContentHash = item.Source.ContentHash ?? assessment.TextFingerprint,
                TechnicalTokens = assessment.ExactTechnicalTokens,
                Coverage = coverage.Distinct(StringComparer.Ordinal).ToList(),
                RankingScore = ApplyBoundedAdjustment(assessment.FinalScore, analysisAdjustment),
                TopicScore = ApplyBoundedAdjustment(assessment.TopicScore, analysisAdjustment),
                EntityScore = assessment.EntityScore,
                TechnicalTokenScore = assessment.TechnicalTokenScore,
                SourceTrust = assessment.SourceTrustScore,
                VersionScore = assessment.VersionScore,
                ConflictPenalty = assessment.ConflictPenalty + Math.Min(0, analysisAdjustment),
                // A populated lexical score identifies a Hybrid V2 retrieval result.
                // Do not synthesize one for legacy/shadow requests: those must keep
                // the pre-existing C#/Rust shared score until V2 is explicitly used.
                LexicalScore = item.Source.LexicalScore ?? 0,
                SemanticScore = item.Source.SemanticScore ?? 0,
                ExactMatchScore = assessment.ExactTechnicalTokens.Count == 0 ? 0 : Math.Min(1, assessment.ExactTechnicalTokens.Count / 3d),
                AliasMatchScore = assessment.FeatureScore,
                ProductMatchScore = assessment.ProductMatch == false ? 0 : 1,
                FeatureMatchScore = assessment.FeatureScore,
                OperationMatchScore = assessment.OperationScore,
                IntentMatchScore = assessment.IntentScore,
                ExplicitlyExcluded = item.IsManuallyExcluded || assessment.ExplicitlyExcluded ||
                    (analysisHowTo && !item.IsManuallySelected &&
                     !HasDirectAnalysisEvidence(item) &&
                     !HasRelevantAnalysisCoverage(coverage, requiredCoverage)),
                TopicConflict = assessment.TopicConflict ||
                    (queryProfile.Features.Count > 0 && !assessment.HasTopicMatch),
                ProductMismatch = assessment.ProductMatch is false,
                IsManuallySelected = item.IsManuallySelected,
                // Coverage-aware evidence is converted to a bounded excerpt before prompt
                // construction. Estimate that same upper bound here so long source chunks do
                // not evict candidates that provide otherwise missing required coverage.
                EstimatedChars = item.Title.Length + Math.Min(CoverageExcerptMaxLength, item.Text.Length),
            };
        }).ToList();

        var request = new CoverageEvidenceSelectionRequest
        {
            RequiredCoverage = requiredCoverage,
            Candidates = candidates,
            BaseMaxItems = Math.Clamp(baseMaxItems, 1, 5),
            ExpansionMaxItems = Math.Clamp(context.CoverageAwareMaxEvidenceItems, 1, 5),
            CharacterBudget = Math.Max(600, context.MaxPromptChars / 2),
            // The UI threshold applies to the upstream search score. The coverage selector uses
            // a separately normalized quality score, so reusing values such as 0.65 here drops
            // relevant past cases before set-level coverage can be evaluated.
            MinimumQualityScore = Math.Min(
                Math.Clamp(minimumScore, 0, 1),
                CoverageSelectorMinimumQualityCeiling),
        };
        var execution = CoverageEvidenceSelectorCoordinator.Select(request, new RustEvidenceSelectorOptions
        {
            UseRustEvidenceSelector = context.UseRustEvidenceSelector,
            UsePersistentRustEvidenceSelector = context.UsePersistentRustEvidenceSelector,
            MaxWorkerRestartsPerMinute = context.MaxWorkerRestartsPerMinute,
            EnableRustSelectorShadowMode = context.EnableRustSelectorShadowMode,
            TimeoutMs = context.RustEvidenceSelectorTimeoutMs,
            ExecutablePath = context.RustEvidenceSelectorExecutablePath,
            RankingMode = context.RankingMode,
            ShadowMinimumRunsForReadiness = context.ShadowMinimumRunsForReadiness,
            ShadowMaxStoredRecords = context.ShadowMaxStoredRecords,
            ShadowObservationFilePath = context.RustShadowObservationFilePath,
        }, workerClient: context.RustEvidenceSelectorWorkerClient);
        var selection = execution.Selection;
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
            new QuestionClassifier().Classify(context.InquiryText).QuestionTypes,
            execution);
    }

    private static SearchSourceSelectionResult BuildResult(
        IReadOnlyList<SearchSourceViewModel> allItems,
        IReadOnlyList<SearchSourceViewModel> selectedItems,
        IReadOnlyList<SearchSourceViewModel> selected,
        CoverageEvidenceSelectionResult selection,
        int baseMaxItems,
        double minimumScore,
        IReadOnlyList<string> questionTypes,
        CoverageEvidenceSelectorExecution? execution = null)
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
            SelectorEngine = execution?.Engine ?? "CSharp",
            RustSelectorElapsedMilliseconds = execution?.RustElapsedMilliseconds ?? 0,
            RustSelectorReportedElapsedMilliseconds = execution?.RustSelectorElapsedMilliseconds ?? 0,
            CSharpSelectorElapsedMilliseconds = execution?.CSharpElapsedMilliseconds ?? 0,
            RustSelectorFallbackReason = execution?.FallbackReason ?? string.Empty,
            RustSelectorParityValidation = execution?.ParityValidation ?? "not applicable",
            RustShadowStatistics = execution?.ShadowStatistics,
            PersistentRustWorkerHealth = execution?.PersistentWorkerHealth,
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

    private static double AnalysisSelectionAdjustment(
        TopicEntityProfile queryProfile,
        SearchSourceViewModel item)
    {
        if (!queryProfile.Operations.Contains("Analysis", StringComparer.Ordinal))
        {
            return 0;
        }

        var heading = string.Join(' ', item.Title, item.Source.SectionTitle);
        var text = DocumentText(item);
        var operationInHeading = ContainsAny(
            heading,
            "qacli analyze", "analyze project", "project analysis",
            "プロジェクトを解析", "プロジェクトの解析", "解析を実行");
        var unrelatedHeading = ContainsAny(
            heading,
            "Dashboard", "ダッシュボード",
            "License", "ライセンス",
            "IDE", "Visual Studio", "Eclipse",
            "Backup", "バックアップ",
            "Installation", "インストール",
            "qacli validate build", "qacli validate cibuild",
            "upload", "アップロード");

        if (unrelatedHeading && !operationInHeading)
        {
            return -0.55;
        }

        if (operationInHeading)
        {
            return 0.30;
        }

        if (ContainsAnalysisGuiProcedure(text))
        {
            return 0.55;
        }

        if (ContainsValidateWorkflow(text) && !HasDirectAnalysisEvidence(item))
        {
            return -0.60;
        }

        if (ContainsAny(text, "qacli analyze", "qaclianalyze"))
        {
            return 0.24;
        }

        if (ContainsAny(
            text,
            "analyze project", "project analysis", "run analysis", "execute analysis",
            "プロジェクトを解析", "プロジェクトの解析", "解析を実行", "解析開始"))
        {
            return 0.12;
        }

        return -0.35;
    }

    private static bool ContainsValidateWorkflow(string value) =>
        ContainsAny(value, "qacli validate", "validate build", "validate cibuild") ||
        (value.Contains("Validate", StringComparison.OrdinalIgnoreCase) &&
         ContainsAny(value, "upload", "アップロード"));

    private static bool HasDirectAnalysisEvidence(SearchSourceViewModel item)
    {
        var text = DocumentText(item);
        if (ContainsValidateWorkflow(text) && !ContainsAnalysisGuiProcedure(text))
        {
            return false;
        }

        if (ContainsAny(text, "qacli project upgrade", "qacliprojectupgrade", "projectupgrade") &&
            !ContainsAnalysisGuiProcedure(text) &&
            !ContainsAny(text, "qacli analyze", "qaclianalyze"))
        {
            return false;
        }

        var coverage = CoverageAnalyzer.ObserveForCoverageSelection(text);
        return ContainsAnalysisGuiProcedure(text) ||
            ContainsAny(text, "qacli analyze", "qaclianalyze", "Analyze Project", "Run Analysis") ||
            ContainsAnalysisPreparation(text) ||
            (coverage.Contains(CoverageAnalyzer.AnalysisVerification) && !ContainsValidateWorkflow(text));
    }

    private static bool HasRelevantAnalysisCoverage(
        IEnumerable<string> coverage,
        IReadOnlyList<string> requiredCoverage) =>
        coverage.Any(requiredCoverage.Contains);

    private static bool ContainsAnalysisPreparation(string value) =>
        ContainsAny(value, "解析", "analyze", "analysis") &&
        ContainsAny(value, "プロジェクト", "project") &&
        ContainsAny(
            value,
            "ソースファイル", "source file", "コンパイラ", "compiler", "CCT", "project setup");

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAnalysisGuiProcedure(string value) =>
        ContainsAny(
            value,
            "]>[解析]>", "]>[解析(", "］＞［解析］＞", "［解析（",
            "プロジェクト全体のファイルベース解析", "Analyze Project", "Run Analysis") ||
        (ContainsAny(value, "QAGUIで", "QA GUIで", "GUIで") &&
         ContainsAny(value, "解析を実行", "解析を開始", "ファイルベース解析を実行"));

    private static double ApplyBoundedAdjustment(double score, double adjustment)
    {
        var normalized = Math.Clamp(score, 0, 1);
        return adjustment >= 0
            ? normalized + ((1 - normalized) * Math.Clamp(adjustment, 0, 0.95))
            : normalized * (1 + Math.Clamp(adjustment, -1, 0));
    }

    private static bool IsSourceType(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsPastEvidenceSourceType(string? sourceType) => sourceType is not null &&
        (sourceType.Equals("PastCaseNote", StringComparison.OrdinalIgnoreCase) ||
         sourceType.Equals("PastAnswer", StringComparison.OrdinalIgnoreCase) ||
         sourceType.Equals("ExactPastAnswer", StringComparison.OrdinalIgnoreCase));

    private static (int? OverviewCandidateIndex, int? ConfigurationCandidateIndex) AssignStreamCoverageRoles(
        IReadOnlyList<SearchSourceViewModel> items,
        IReadOnlyDictionary<int, TopicEntityRankingAssessment> assessments)
    {
        var eligible = items
            .Select((item, index) => new
            {
                Item = item,
                Index = index,
                Assessment = assessments[index],
                Text = DocumentText(item),
            })
            .Where(candidate =>
                candidate.Assessment.HasTopicMatch &&
                !candidate.Item.IsManuallyExcluded &&
                !IsPastEvidenceSourceType(candidate.Item.SourceType))
            .ToList();
        if (eligible.Count == 0)
        {
            return (null, null);
        }

        var configuration = eligible
            .OrderByDescending(candidate => StreamConfigurationScore(candidate.Text))
            .ThenByDescending(candidate => candidate.Assessment.FinalScore)
            .ThenBy(candidate => candidate.Index)
            .First();
        var overview = eligible
            .Where(candidate => candidate.Index != configuration.Index)
            .OrderByDescending(candidate => StreamOverviewScore(candidate.Text))
            .ThenByDescending(candidate => candidate.Assessment.FinalScore)
            .ThenBy(candidate => candidate.Index)
            .FirstOrDefault();

        return (overview?.Index ?? configuration.Index, configuration.Index);
    }

    private static int StreamOverviewScore(string text) =>
        CountOccurrences(text, "トラッキング") * 6 +
        CountOccurrences(text, "追跡") * 5 +
        CountOccurrences(text, "新しい問題") * 5 +
        CountOccurrences(text, "変更") * 2 +
        CountOccurrences(text, "機能") * 2 +
        CountOccurrences(text, "目的") * 2 +
        CountOccurrences(text, "track") * 4;

    private static int StreamConfigurationScore(string text) =>
        CountOccurrences(text, "設定") * 5 +
        CountOccurrences(text, "作成") * 3 +
        CountOccurrences(text, "生成") * 3 +
        CountOccurrences(text, "接合") * 5 +
        CountOccurrences(text, "結合") * 5 +
        CountOccurrences(text, "オプション") * 3 +
        CountOccurrences(text, "コマンド") * 2 +
        CountOccurrences(text, "configure") * 4 +
        CountOccurrences(text, "create") * 3;

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = text.IndexOf(value, startIndex, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }

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
