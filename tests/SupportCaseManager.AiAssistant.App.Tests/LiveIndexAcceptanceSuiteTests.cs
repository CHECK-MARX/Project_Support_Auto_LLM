using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Ranking;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class LiveIndexAcceptanceSuiteTests
{
    private static readonly AcceptanceCase[] Cases =
    [
        new("A", "QACプロジェクト解析手順", "QACで、プロジェクトを解析するための手順を教えてください。", ["解析", "analy"]),
        new("B", "Validate Stream概要と設定", "Validateのストリーム機能についてどのような機能かを教えてください。また、設定方法について教えてください。", ["stream", "ストリーム"]),
        new("C", "QACからValidateへCLIアップロード", "QACの解析結果をValidateへCLIでアップロードする手順を教えてください。", ["validate", "upload", "アップロード"]),
        new("D", "Validate権限不足", "Validateへ解析結果をアップロードすると権限不足になります。確認手順を教えてください。", ["権限", "permission", "validate"]),
        new("E", "Visual Studio連携", "Helix QACをVisual Studioと連携して解析する方法を教えてください。", ["visual studio", "連携", "plugin"]),
        new("F", "ライセンス設定", "Helix QACのライセンスサーバーとポートの設定方法を教えてください。", ["license", "ライセンス", "port", "ポート"]),
        new("G", "解析結果確認", "QACの解析が完了したことと解析結果を確認する方法を教えてください。", ["結果", "result", "解析"]),
        new("H", "CLI option", "qacli analyzeの-Pオプションの用途と指定方法を教えてください。", ["-p", "qacli", "analyze"]),
        new("I", "バージョン差異", "QAC 2025.4と2026.1でプロジェクト解析手順に差があるか教えてください。", ["2025.4", "2026.1", "解析"]),
        new("J", "根拠不足", "QACで量子暗号鍵を自動生成する設定方法を教えてください。", ["量子暗号", "quantum"]),
    ];

    [Fact]
    public async Task ActualIndex_RunsTenSafeAcceptanceCasesWithPersistentRust()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SCM_RUN_LIVE_ACCEPTANCE"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var rustExecutable = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE") ?? string.Empty;
        Assert.True(File.Exists(rustExecutable), $"Rust selector executable was not found: {rustExecutable}");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsPath = Environment.GetEnvironmentVariable("SCM_LIVE_SETTINGS_PATH") ??
            Path.Combine(localAppData, "SupportCaseManager", "ai-data", "settings.json");
        var settings = JsonSerializer.Deserialize<AiAssistantSettings>(
            await File.ReadAllTextAsync(settingsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidOperationException("AI settings could not be loaded.");
        var product = settings.Products.First(item =>
            string.Equals(item.ProductName, "HelixQAC", StringComparison.OrdinalIgnoreCase));
        var aiIndexFolder = string.IsNullOrWhiteSpace(settings.AiIndexFolder)
            ? Path.Combine(localAppData, "SupportCaseManager", "ai-index")
            : settings.AiIndexFolder;
        var search = new ProductScopedSearchService(
            new AiCaseKeywordSearcher(),
            new AiManualKeywordSearcher());
        using var worker = new RustEvidenceSelectorWorkerClient();
        var results = new List<object>();
        var metricRows = new List<AcceptanceMetric>();

        foreach (var testCase in Cases)
        {
            var context = new CaseContext { ProductName = product.ProductName };
            var focus = new InquiryFocusExtractor().Extract(
                testCase.Question,
                context,
                usePhase175QualityControls: true);
            var candidates = await search.SearchAllHybridAsync(
                product,
                aiIndexFolder,
                focus,
                settings.LlmProvider,
                maxResults: 36);
            var viewModels = candidates
                .Select((source, index) => new SearchSourceViewModel(source, isSelected: index < 5))
                .ToList();
            var selection = SearchSourceSelectionBuilder.Build(
                viewModels,
                maxEvidenceItems: 3,
                autoSelectMinimumScore: settings.AutoSelectMinimumScore,
                enableTopNFallback: true,
                questionAwareContext: new QuestionAwareEvidenceSelectionContext
                {
                    Enabled = true,
                    InquiryText = testCase.Question,
                    ProductName = product.ProductName,
                    RankingMode = EvidenceRankingModes.Phase16,
                    UsePhase175QualityControls = true,
                    UseCoverageAwareEvidenceSelection = true,
                    CoverageAwareMaxEvidenceItems = 5,
                    MaxPromptChars = Math.Max(settings.MaxPromptChars, 10000),
                    UseRustEvidenceSelector = true,
                    UsePersistentRustEvidenceSelector = true,
                    RustEvidenceSelectorWorkerClient = worker,
                    RustEvidenceSelectorExecutablePath = rustExecutable,
                    RustEvidenceSelectorTimeoutMs = 5000,
                });
            var request = new AnswerDraftRequest
            {
                Case = context,
                InquiryText = testCase.Question,
                InquiryFocus = focus,
                Sources = selection.Sources,
                Settings = settings with
                {
                    MaxEvidenceItems = Math.Max(3, selection.Sources.Count),
                    MaxPromptChars = Math.Max(settings.MaxPromptChars, 10000),
                    UseAnswerQualityGate = true,
                    UsePhase175QualityControls = true,
                    UseCoverageAwareEvidenceSelection = true,
                    CoverageAwareMaxEvidenceItems = 5,
                },
                RequestedAt = DateTimeOffset.Now,
            };
            var answerService = new AiAnswerService(
                new PromptBuilder(),
                new EvidenceBuilder(),
                new SafetyRedactionService(),
                new MalformedResponseLlmClient());
            var answer = await answerService.GenerateDraftAsync(request);
            var profile = TopicEntityAnalyzer.Extract(
                testCase.Question,
                SupportTopicCatalog.Create(product.ProductName));
            var expectationMet = testCase.Id == "J"
                ? !selection.Sources.Any(source => ContainsAny(SourceText(source), testCase.ExpectedTerms))
                : selection.Sources.Any(source => ContainsAny(SourceText(source), testCase.ExpectedTerms));
            var fallbackUsed = answer.Warnings.Any(warning =>
                warning.Contains("補正", StringComparison.Ordinal) ||
                warning.Contains("補完", StringComparison.Ordinal) ||
                warning.Contains("解析に失敗", StringComparison.Ordinal));
            var metric = new AcceptanceMetric(
                testCase.Id,
                expectationMet,
                answer.AnswerQuality?.Decision ?? AnswerQualityDecisions.NeedsReview,
                selection.SelectorEngine,
                fallbackUsed,
                selection.Sources.Count,
                selection.Sources.Count(static source => IsType(source, "Manual")),
                selection.Sources.Count(static source => IsType(source, "OfficialDoc")),
                selection.Sources.Count(static source => IsPastCase(source.SourceType)),
                selection.Sources.Count(static source => source.PageNumber is > 0),
                selection.Sources.Count(static source => !string.IsNullOrWhiteSpace(source.SectionTitle)),
                selection.RequiredCoverage.Count == 0 ||
                    selection.RequiredCoverage.All(selection.FinalCoverage.Contains));
            metricRows.Add(metric);
            results.Add(new
            {
                testCase.Id,
                testCase.Name,
                testCase.Question,
                Topic = profile.Features,
                Operation = profile.Operations,
                CandidateCount = candidates.Count,
                CandidateSourceTypeCounts = candidates
                    .GroupBy(static source => source.SourceType)
                    .ToDictionary(static group => group.Key, static group => group.Count()),
                SelectorEngine = selection.SelectorEngine,
                selection.RustSelectorFallbackReason,
                selection.RequiredCoverage,
                selection.FinalCoverage,
                EvidenceExpectationMet = expectationMet,
                Evidence = selection.Sources.Select(source => new
                {
                    SourceId = IsPastCase(source.SourceType)
                        ? RustSelectorPrivacy.HashEvidenceId(source.SourceId ?? string.Empty)
                        : source.SourceId,
                    source.SourceType,
                    source.Score,
                    SelectionReason = source.ScoreBreakdown,
                    DocumentTitle = IsPastCase(source.SourceType)
                        ? "[internal past case]"
                        : source.DocumentTitle,
                    source.PageNumber,
                    source.SectionTitle,
                    source.Url,
                    Coverage = CoverageAnalyzer.ObserveForCoverageSelection(SourceText(source)),
                }),
                AnswerQualityDecision = answer.AnswerQuality?.Decision ?? AnswerQualityDecisions.NeedsReview,
                answer.AnswerQuality?.EvidenceCoverage,
                answer.AnswerQuality?.AnswerCoverage,
                answer.AnswerQuality?.MissingEvidenceCoverage,
                answer.AnswerQuality?.MissingAnswerCoverage,
                FallbackUsed = fallbackUsed,
                GeneratedAnswer = answer.CustomerReplyDraft,
                HumanEditScore = (int?)null,
            });

            Assert.Equal("PersistentRust", selection.SelectorEngine);
            Assert.Contains("[会社名]", answer.CustomerReplyDraft, StringComparison.Ordinal);
            Assert.Contains("[お客様名] 様", answer.CustomerReplyDraft, StringComparison.Ordinal);
            var recipientHeader = string.Join(
                '\n',
                answer.CustomerReplyDraft.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Take(2));
            Assert.DoesNotContain("TOYO", recipientHeader, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("東陽テクニカ", recipientHeader, StringComparison.OrdinalIgnoreCase);
        }

        var evidenceTotal = metricRows.Sum(static item => item.EvidenceCount);
        var report = new
        {
            GeneratedAt = DateTimeOffset.Now,
            SettingsPath = settingsPath,
            IndexPath = aiIndexFolder,
            RustExecutable = rustExecutable,
            CaseCount = metricRows.Count,
            Summary = new
            {
                CorrectEvidenceSelectionRate = Rate(metricRows.Count(static item => item.ExpectationMet), metricRows.Count),
                ManualUsageRate = Rate(metricRows.Sum(static item => item.ManualCount), evidenceTotal),
                OfficialDocUsageRate = Rate(metricRows.Sum(static item => item.OfficialCount), evidenceTotal),
                PastCaseUsageRate = Rate(metricRows.Sum(static item => item.PastCaseCount), evidenceTotal),
                PageNumberPresentationRate = Rate(metricRows.Sum(static item => item.PageCount), evidenceTotal),
                SectionTitlePresentationRate = Rate(metricRows.Sum(static item => item.SectionCount), evidenceTotal),
                RequiredCoverageAchievementRate = Rate(metricRows.Count(static item => item.RequiredCoverageMet), metricRows.Count),
                CustomerReadyRate = DecisionRate(AnswerQualityDecisions.CustomerReady),
                NeedsReviewRate = DecisionRate(AnswerQualityDecisions.NeedsReview),
                InsufficientEvidenceRate = DecisionRate(AnswerQualityDecisions.InsufficientEvidence),
                FallbackRate = Rate(metricRows.Count(static item => item.FallbackUsed), metricRows.Count),
                RustUsageRate = Rate(metricRows.Count(static item => item.SelectorEngine == "PersistentRust"), metricRows.Count),
                CSharpFallbackRate = Rate(metricRows.Count(static item => item.SelectorEngine is "CSharp" or "RustFallback"), metricRows.Count),
            },
            PersistentWorkerHealth = worker.GetHealth(),
            Cases = results,
        };
        var reportPath = Environment.GetEnvironmentVariable("SCM_LIVE_ACCEPTANCE_REPORT") ??
            Path.Combine(Path.GetTempPath(), "SupportCaseManager", "live-index-acceptance.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(Cases.Length, metricRows.Count);
        Assert.All(metricRows, item => Assert.Equal("PersistentRust", item.SelectorEngine));

        double DecisionRate(string decision) =>
            Rate(metricRows.Count(item => item.Decision == decision), metricRows.Count);
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> terms) =>
        terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string SourceText(SearchSource source) =>
        string.Join('\n', source.DocumentTitle, source.Title, source.SectionTitle, source.Text);

    private static bool IsType(SearchSource source, string sourceType) =>
        string.Equals(source.SourceType, sourceType, StringComparison.OrdinalIgnoreCase);

    private static bool IsPastCase(string sourceType) =>
        sourceType.Equals("PastCase", StringComparison.OrdinalIgnoreCase) ||
        sourceType.Equals("PastCaseNote", StringComparison.OrdinalIgnoreCase) ||
        sourceType.Equals("PastAnswer", StringComparison.OrdinalIgnoreCase);

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0 : Math.Round((double)numerator / denominator, 4);

    private sealed record AcceptanceCase(
        string Id,
        string Name,
        string Question,
        IReadOnlyList<string> ExpectedTerms);

    private sealed record AcceptanceMetric(
        string Id,
        bool ExpectationMet,
        string Decision,
        string SelectorEngine,
        bool FallbackUsed,
        int EvidenceCount,
        int ManualCount,
        int OfficialCount,
        int PastCaseCount,
        int PageCount,
        int SectionCount,
        bool RequiredCoverageMet);

    private sealed class MalformedResponseLlmClient : ILlmClient
    {
        public Task<LlmGenerationResult> GenerateAsync(
            PromptMessages messages,
            LlmProviderSettings settings,
            bool disableThinking = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmGenerationResult
            {
                Content = "{\"customerReplyDraft\":",
                DoneReason = "length",
            });
    }
}
