using System.Diagnostics;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Ranking;
using SupportCaseManager.AiAssistant.App.ViewModels;
using Xunit.Abstractions;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase18EvidenceSelectionIntegrationTests
{
    private readonly ITestOutputHelper output;

    public Phase18EvidenceSelectionIntegrationTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void FeatureOff_PreservesLegacySelection()
    {
        var items = new[]
        {
            Item("a", 0.9, "same"),
            Item("b", 0.8, "same"),
            Item("c", 0.7, "different"),
        };

        var legacy = SearchSourceSelectionBuilder.Build(items, 2, 0.3);
        var featureOff = SearchSourceSelectionBuilder.Build(items, 2, 0.3, questionAwareContext: new()
        {
            Enabled = false,
            UseCoverageAwareEvidenceSelection = false,
        });

        Assert.Equal(
            legacy.Sources.Select(static source => source.SourceId),
            featureOff.Sources.Select(static source => source.SourceId));
        Assert.Equal(legacy.Warning, featureOff.Warning);
        Assert.Empty(featureOff.SelectionMode);
    }

    [Fact]
    public void FeatureOn_UsesLowerRankedEvidenceToCompleteCoverageAndSkipsDuplicate()
    {
        var items = new[]
        {
            Item("upload-a", 0.90, "Perforce QAC Validate qacli validate build --build-name BUILD --project PROJECT upload option"),
            Item("upload-duplicate", 0.89, "Perforce QAC Validate qacli validate build --build-name BUILD --project PROJECT upload option"),
            Item("auth", 0.75, "Perforce QAC Validate qacli auth token, validate connect server URL, project association"),
            Item("incremental", 0.70, "Perforce QAC Validate qacli validate ibuild incremental build"),
            Item("verify", 0.68, "Perforce QAC Validate portal verification, upload failed error log troubleshooting"),
        };

        var result = SearchSourceSelectionBuilder.Build(items, 3, 0.10, questionAwareContext: Context(5));

        Assert.DoesNotContain(result.Sources, static source => source.SourceId == "upload-duplicate");
        Assert.Contains(result.Sources, static source => source.SourceId == "auth");
        Assert.Contains(result.Sources, static source => source.SourceId == "incremental");
        Assert.Contains(result.Sources, static source => source.SourceId == "verify");
        Assert.Equal("CoverageAware", result.SelectionMode);
        Assert.Equal("Phase18CoverageAware", result.RankingMode);
        Assert.True(result.Sources.Count is >= 3 and <= 5);
    }

    [Fact]
    public void FeatureOn_RetainsManualSelectionsAboveConfiguredLimitAndWarns()
    {
        var items = Enumerable.Range(1, 4)
            .Select(index => Item($"manual-{index}", 0.1, $"Perforce QAC Validate qacli validate build --option-{index}"))
            .ToList();
        foreach (var item in items)
        {
            item.IsSelected = false;
            item.IsSelected = true;
        }

        var result = SearchSourceSelectionBuilder.Build(items, 3, 0.8, questionAwareContext: Context(3));

        Assert.Equal(4, result.Sources.Count);
        Assert.Contains("ManualSelectionExceedsLimit", result.Warning);
    }

    [Fact]
    public void FeatureOn_DoesNotReSelectManuallyExcludedEvidence()
    {
        var excluded = Item("license", 0.99, "Validate Stream license configuration verification");
        excluded.IsSelected = false;
        var relevant = Item("stream", 0.65, "Validate Stream overview purpose create configuration QAC association verification");

        var result = SearchSourceSelectionBuilder.Build(
            [excluded, relevant],
            3,
            0.10,
            questionAwareContext: new QuestionAwareEvidenceSelectionContext
            {
                Enabled = true,
                InquiryText = "Validate Streamの概要、目的、作成、設定、QAC関連付け、確認方法。ライセンスは対象外。",
                ProductName = "HelixQAC",
                UsePhase175QualityControls = true,
                UseCoverageAwareEvidenceSelection = true,
                CoverageAwareMaxEvidenceItems = 5,
                MaxPromptChars = 6000,
            });

        Assert.DoesNotContain(result.Sources, static source => source.SourceId == "license");
        Assert.Contains(result.Sources, static source => source.SourceId == "stream");
    }

    [Fact]
    public void StreamOverviewAndSetup_AutomaticallyUsesCoverageSelectionOnlyForCompoundFeatureQuestion()
    {
        Assert.True(CoverageAwareSearchSourceSelector.ShouldApplyAutomatically(
            "Validateのストリーム機能についてどのような機能かを教えてください。また、設定方法について教えてください。",
            "HelixQAC"));
        Assert.False(CoverageAwareSearchSourceSelector.ShouldApplyAutomatically(
            "QACの解析結果をqacli validate buildでアップロードする方法を教えてください。",
            "HelixQAC"));
        Assert.False(CoverageAwareSearchSourceSelector.ShouldApplyAutomatically(
            "Validateのバックアップ手順を教えてください。",
            "HelixQAC"));
    }

    [Fact]
    public void StreamOverviewAndSetup_MaximizesCoverageAndRetainsRelevantPastEvidence()
    {
        const string question = "Validateのストリーム機能についてどのような機能かを教えてください。また、設定方法について教えてください。";
        var items = new[]
        {
            Item("backup", 0.99, "Validateの一般的なバックアップ手順と復元方法です。", "Manual"),
            Item("overview", 0.61, "Validate Streamの機能概要です。開発中の変更とビルド履歴を追跡します。", "OfficialDoc"),
            Item("setup", 0.58, "Validate ストリームの設定方法です。Streamを作成してプロジェクトを設定します。", "Manual"),
            Item("past", 0.41, "過去案件ではValidateストリームを設定し、対象プロジェクトの履歴を確認しました。", "PastCaseNote"),
            Item("generic", 0.88, "Validateの利用手順とプロジェクト設定の一般説明です。", "Manual"),
        };

        var result = SearchSourceSelectionBuilder.Build(
            items,
            3,
            0.65,
            questionAwareContext: Phase18Context(question, 3));

        Assert.Equal(3, result.Sources.Count);
        Assert.Contains(result.Sources, static source => source.SourceId == "overview");
        Assert.Contains(result.Sources, static source => source.SourceId == "setup");
        Assert.Contains(result.Sources, static source => source.SourceId == "past");
        Assert.DoesNotContain(result.Sources, static source => source.SourceId is "backup" or "generic");
        Assert.Contains(CoverageAnalyzer.PriorCaseSupplement, result.RequiredCoverage);
        Assert.Contains(CoverageAnalyzer.PriorCaseSupplement, result.FinalCoverage);
        Assert.Equal(1, result.PastCaseNoteSendCount);
        Assert.Equal(1, result.ManualSendCount);
        Assert.Equal(1, result.OfficialDocSendCount);
    }

    [Fact]
    public void E2eA_QacToValidateCli_CoverageDoesNotRegressFromPhase175()
    {
        const string question = "QAC解析結果をCLIからValidateへアップロードするため、auth、接続、プロジェクト関連付け、build-name、incremental build、確認方法、エラー対処を知りたい。";
        var items = new[]
        {
            Item("upload", 0.95, "Perforce QAC Validate qacli validate build --qaf-project . --build-name BUILD upload option"),
            Item("upload-copy", 0.94, "Perforce QAC Validate qacli validate build --qaf-project . --build-name BUILD upload option"),
            Item("auth", 0.90, "Perforce QAC Validate qacli auth token, qacli validate connect server URL, project association"),
            Item("incremental", 0.70, "Perforce QAC Validate qacli validate ibuild incremental build"),
            Item("verify", 0.65, "Perforce QAC Validate portal build verification, failed upload error log troubleshooting"),
        };

        var phase175Watch = Stopwatch.StartNew();
        var phase175 = SearchSourceSelectionBuilder.Build(
            items,
            3,
            0.10,
            questionAwareContext: Phase175Context(question));
        phase175Watch.Stop();
        var phase18Watch = Stopwatch.StartNew();
        var phase18 = SearchSourceSelectionBuilder.Build(
            items,
            3,
            0.10,
            questionAwareContext: Phase18Context(question, 5));
        phase18Watch.Stop();

        var required = RequiredCoverage(question);
        var phase175Coverage = RequiredCoverageObserved(phase175.Sources, required);
        var phase18Coverage = RequiredCoverageObserved(phase18.Sources, required);
        var phase175Missing = required.Except(phase175Coverage, StringComparer.Ordinal).ToList();
        var phase18Missing = required.Except(phase18Coverage, StringComparer.Ordinal).ToList();
        var answer = "qacli authで認証し、qacli validate connectで接続してValidateプロジェクトを関連付けます。qacli validate build --qaf-project . --build-name BUILDで登録し、増分時はqacli validate ibuildを実行します。Validate portalでBuildを確認し、失敗時はエラーログを確認します。";
        var phase175Quality = EvaluateAnswer(question, answer, phase175.Sources);
        var phase18Quality = EvaluateAnswer(question, answer, phase18.Sources);

        output.WriteLine(ComparisonLine("E2E-A Phase17.5", phase175, required, phase175Coverage, phase175Missing, phase175Watch.Elapsed, phase175Quality.Decision));
        output.WriteLine(ComparisonLine("E2E-A Phase18", phase18, required, phase18Coverage, phase18Missing, phase18Watch.Elapsed, phase18Quality.Decision));

        Assert.True(phase18Coverage.Count >= phase175Coverage.Count);
        Assert.True(phase18Missing.Count <= phase175Missing.Count);
        Assert.Empty(phase18Missing);
        Assert.Equal(4, phase18.Sources.Count);
        Assert.DoesNotContain(phase18.Sources, static source => source.SourceId == "upload-copy");
        Assert.True(RedundancyCount(phase18.Sources) <= RedundancyCount(phase175.Sources));
        Assert.True(DecisionSeverity(phase18Quality.Decision) <= DecisionSeverity(phase175Quality.Decision));
    }

    [Fact]
    public void E2eB_ValidateStream_CoversRequiredTopicsWithoutExcludedEvidence()
    {
        const string question = "Validate Streamの概要、目的、作成、設定、QAC関連付け、確認方法を教えてください。ライセンスとIDEプラグインは対象外です。";
        var items = new[]
        {
            Item("license", 0.99, "Validate Stream license ライセンス configuration verification"),
            Item("ide", 0.98, "Validate Stream IDE Plugin Eclipse プラグイン configuration"),
            Item("overview", 0.82, "Validate Streamの概要 overview、目的 purpose、利用方法"),
            Item("creation", 0.78, "Validate Streamを作成 createし、設定 configurationを行います"),
            Item("association", 0.74, "Validate StreamとPerforce QACを関連付け associationし、確認 verificationします"),
        };

        var phase175Watch = Stopwatch.StartNew();
        var phase175 = SearchSourceSelectionBuilder.Build(
            items,
            3,
            0.10,
            questionAwareContext: Phase175Context(question));
        phase175Watch.Stop();
        var phase18Watch = Stopwatch.StartNew();
        var phase18 = SearchSourceSelectionBuilder.Build(
            items,
            3,
            0.10,
            questionAwareContext: Phase18Context(question, 5));
        phase18Watch.Stop();

        var required = RequiredCoverage(question);
        var phase175Coverage = RequiredCoverageObserved(phase175.Sources, required);
        var phase18Coverage = RequiredCoverageObserved(phase18.Sources, required);
        var phase175Missing = required.Except(phase175Coverage, StringComparer.Ordinal).ToList();
        var phase18Missing = required.Except(phase18Coverage, StringComparer.Ordinal).ToList();

        output.WriteLine(ComparisonLine("E2E-B Phase17.5", phase175, required, phase175Coverage, phase175Missing, phase175Watch.Elapsed, "not-evaluated"));
        output.WriteLine(ComparisonLine("E2E-B Phase18", phase18, required, phase18Coverage, phase18Missing, phase18Watch.Elapsed, "not-evaluated"));

        Assert.Empty(phase18Missing);
        Assert.Equal(6, phase18Coverage.Count);
        Assert.DoesNotContain(phase18.Sources, static source => source.SourceId is "license" or "ide");
        Assert.True(phase18Coverage.Count >= phase175Coverage.Count);
        Assert.True(phase18Missing.Count <= phase175Missing.Count);
    }

    private static QuestionAwareEvidenceSelectionContext Context(int maxItems) => new()
    {
        Enabled = true,
        InquiryText = "QAC解析結果をCLIからValidateへアップロードするため、auth、接続、プロジェクト関連付け、build-name、incremental build、確認方法、エラー対処を知りたい。",
        ProductName = "HelixQAC",
        UsePhase175QualityControls = true,
        UseCoverageAwareEvidenceSelection = true,
        CoverageAwareMaxEvidenceItems = maxItems,
        MaxPromptChars = 12000,
    };

    private static QuestionAwareEvidenceSelectionContext Phase175Context(string question) => new()
    {
        Enabled = true,
        InquiryText = question,
        ProductName = "HelixQAC",
        RankingMode = EvidenceRankingModes.Phase16,
        UsePhase175QualityControls = true,
        UseCoverageAwareEvidenceSelection = false,
        MaxPromptChars = 12000,
    };

    private static QuestionAwareEvidenceSelectionContext Phase18Context(string question, int maxItems) =>
        Phase175Context(question) with
        {
            UseCoverageAwareEvidenceSelection = true,
            CoverageAwareMaxEvidenceItems = maxItems,
        };

    private static IReadOnlyList<string> RequiredCoverage(string question)
    {
        var catalog = SupportTopicCatalog.Create("HelixQAC");
        var profile = NegationAwareTopicAnalyzer.Analyze(question, catalog).PrimaryProfile;
        return CoverageAnalyzer.RequiredForCoverageSelection(question, profile);
    }

    private static IReadOnlySet<string> ExactCoverage(IEnumerable<SearchSource> sources)
    {
        var coverage = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            coverage.UnionWith(CoverageAnalyzer.ObserveForCoverageSelection($"{source.Title}\n{source.Text}"));
        }

        return coverage;
    }

    private static IReadOnlySet<string> RequiredCoverageObserved(
        IEnumerable<SearchSource> sources,
        IReadOnlyList<string> required) =>
        ExactCoverage(sources)
            .Intersect(required, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

    private static int DecisionSeverity(string decision) => decision switch
    {
        AnswerQualityDecisions.CustomerReady => 0,
        AnswerQualityDecisions.NeedsReview => 1,
        AnswerQualityDecisions.InsufficientEvidence => 2,
        AnswerQualityDecisions.Blocked => 3,
        _ => 4,
    };

    private static AnswerQualityEvaluationResult EvaluateAnswer(
        string question,
        string answer,
        IReadOnlyList<SearchSource> sources) =>
        AnswerQualityEvaluator.Evaluate(new AnswerQualityEvaluationInput
        {
            Question = question,
            Answer = answer,
            ProductName = "HelixQAC",
            Evidence = sources.Select(static source => new AnswerQualityEvidence
            {
                SourceId = source.SourceId,
                SourceType = source.SourceType,
                Text = source.Text,
                ProductName = source.ProductName,
            }).ToList(),
            Catalog = AnswerQualityEvaluator.CreateSupportCatalog("HelixQAC"),
            UseSeparatedCoverage = true,
        });

    private static string ComparisonLine(
        string name,
        SearchSourceSelectionResult result,
        IReadOnlyList<string> required,
        IReadOnlySet<string> coverage,
        IReadOnlyList<string> missing,
        TimeSpan elapsed,
        string decision) =>
        $"{name}: evidence={result.Sources.Count}; coverage={coverage.Count}/{required.Count}; " +
        $"missing={string.Join(',', missing)}; redundancy={RedundancyCount(result.Sources)}; " +
        $"chars={EstimatedChars(result.Sources)}; elapsedMs={elapsed.TotalMilliseconds:0.###}; decision={decision}";

    private static int RedundancyCount(IReadOnlyList<SearchSource> sources) =>
        sources.Count - sources
            .Select(static source => source.Text.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static int EstimatedChars(IEnumerable<SearchSource> sources) =>
        sources.Sum(static source => source.Title.Length + source.Text.Length);

    private static SearchSourceViewModel Item(
        string id,
        double score,
        string text,
        string sourceType = "Manual") => new(
        new SearchSource
        {
            SourceId = id,
            SourceType = sourceType,
            ProductName = "HelixQAC",
            Title = id,
            Text = text,
            FilePath = $@"C:\manuals\{id}.txt",
            DocumentId = id,
            SectionTitle = id,
            Score = score,
        },
        isSelected: true);
}
