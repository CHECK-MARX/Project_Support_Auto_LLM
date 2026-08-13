using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public class QuestionAwareEvidenceRankerTests
{
    private const string Query = "QACで解析した結果をValidateへアップロードするコマンドについて教えてください。コマンドについて、一連の手順について具体的に、アップロードが完了してValidateで確認できるところまで行いたいです。";

    [Fact]
    public void Rank_SelectsCommandAndComplementaryProcedureEvidence()
    {
        var result = QuestionAwareEvidenceRanker.Rank(RepresentativeItems(), Context(), 3);

        Assert.Contains(QuestionTypes.CommandQuestion, result.QuestionTypes);
        Assert.Contains(QuestionTypes.HowToQuestion, result.QuestionTypes);
        Assert.Equal("command", result.Ranked[0].Item.SourceId);
        Assert.DoesNotContain(result.Ranked, item => item.Item.SourceId == "surrounding");
        Assert.Contains(QuestionAwareEvidenceRanker.UploadCommand, result.FinalCoverage);
        Assert.Contains(QuestionAwareEvidenceRanker.Authentication, result.FinalCoverage);
        Assert.Contains(QuestionAwareEvidenceRanker.ProjectAssociation, result.FinalCoverage);
        Assert.Contains(QuestionAwareEvidenceRanker.ValidateVerification, result.FinalCoverage);
        Assert.Empty(result.InsufficientReasons);
    }

    [Fact]
    public void ExtractExactTechnicalTokens_DoesNotInventOptions()
    {
        var tokens = QuestionAwareEvidenceRanker.ExtractExactTechnicalTokens("qacli upload --qaf-project Demo");

        Assert.Contains(tokens, item => string.Equals(item, "qacli", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--qaf-project", tokens);
        Assert.DoesNotContain("--build-name", tokens);
    }

    [Fact]
    public void Rank_HandlesProductAndVersionSafety()
    {
        var mismatch = QuestionAwareEvidenceRanker.Rank(
            [Create("wrong", "qacli upload --qaf-project Demo", 1.0, product: "Checkmarx")],
            Context(), 3);
        Assert.Empty(mismatch.Ranked);
        Assert.Contains("ProductMismatch", mismatch.InsufficientReasons);

        var version = QuestionAwareEvidenceRanker.Rank(
            [Create("old", "2024.1 qacli upload --qaf-project Demo", 0.8)],
            Context() with { InquiryText = Query + " 対象バージョンは2025.4です。" }, 3);
        Assert.Equal("mismatch", version.Ranked[0].VersionMatch);
        Assert.Contains("MissingVersionSpecificEvidence", version.InsufficientReasons);
    }

    [Fact]
    public void Rank_KeepsComplementaryChunksAndRemovesExactDuplicateText()
    {
        var first = Create("same-1", "qacli upload --qaf-project Demo", 0.8);
        var second = Create("same-2", "Validateへログインし、完了後にportalで確認します。", 0.7);
        var duplicate = Create("duplicate", first.Text, 0.6);

        var result = QuestionAwareEvidenceRanker.Rank([first, second, duplicate], Context(), 5);

        Assert.Equal(2, result.Ranked.Count);
    }

    [Fact]
    public void SelectionBuilder_DisabledIsLegacyCompatibleAndEnabledReranksFallback()
    {
        var items = RepresentativeItems();
        var legacy = SearchSourceSelectionBuilder.Build(items, 3, enableTopNFallback: true);
        var disabled = SearchSourceSelectionBuilder.Build(
            items, 3, enableTopNFallback: true,
            questionAwareContext: Context() with { Enabled = false });
        Assert.Equal(legacy.Sources.Select(static item => item.SourceId), disabled.Sources.Select(static item => item.SourceId));
        Assert.False(disabled.QuestionAwareSelectionApplied);

        var enabled = SearchSourceSelectionBuilder.Build(
            items, 3, enableTopNFallback: true, questionAwareContext: Context());
        Assert.True(enabled.QuestionAwareSelectionApplied);
        Assert.Equal("command", enabled.Sources[0].SourceId);
    }

    [Fact]
    public void SelectionBuilder_BlocksAutomaticSurroundingOnlyEvidence()
    {
        var result = SearchSourceSelectionBuilder.Build(
            [Create("overview", "Validateの製品概要です。", 0.99, "OfficialDoc")],
            3,
            enableTopNFallback: true,
            questionAwareContext: Context());

        Assert.Empty(result.Sources);
        Assert.Contains("MissingCommand", result.InsufficientEvidenceReasons);
    }

    [Fact]
    public void SelectionBuilder_Phase16PrefersStreamAndPhase15PathRemainsUnchanged()
    {
        var items = new List<SearchSourceViewModel>
        {
            Create("license", "Validate License configuration and setup", 0.99),
            Create("stream", "Validate Stream overview, configuration steps, QAC project association and verification.", 0.45),
        };
        var phase15 = SearchSourceSelectionBuilder.Build(
            items,
            1,
            enableTopNFallback: true,
            questionAwareContext: new QuestionAwareEvidenceSelectionContext
            {
                Enabled = true,
                InquiryText = "Validate Streamの概要と設定方法",
                ProductName = "HelixQAC",
                RankingMode = EvidenceRankingModes.Phase15,
            });
        var phase16 = SearchSourceSelectionBuilder.Build(
            items,
            1,
            enableTopNFallback: true,
            questionAwareContext: new QuestionAwareEvidenceSelectionContext
            {
                Enabled = true,
                InquiryText = "Validate Streamの概要と設定方法",
                ProductName = "HelixQAC",
                RankingMode = EvidenceRankingModes.Phase16,
            });

        Assert.Equal("license", phase15.Sources[0].SourceId);
        Assert.Equal(EvidenceRankingModes.Phase15, phase15.RankingMode);
        Assert.Equal("stream", phase16.Sources[0].SourceId);
        Assert.Equal(EvidenceRankingModes.Phase16, phase16.RankingMode);
    }

    [Fact]
    public void SelectionBuilder_Phase16OffMatchesExistingPhase15Exactly()
    {
        var items = RepresentativeItems();
        var existing = SearchSourceSelectionBuilder.Build(
            items, 3, enableTopNFallback: true, questionAwareContext: Context());
        var explicitPhase15 = SearchSourceSelectionBuilder.Build(
            items,
            3,
            enableTopNFallback: true,
            questionAwareContext: Context() with { RankingMode = EvidenceRankingModes.Phase15 });

        Assert.Equal(
            existing.Sources.Select(static source => source.SourceId),
            explicitPhase15.Sources.Select(static source => source.SourceId));
        Assert.Equal(existing.FinalCoverage, explicitPhase15.FinalCoverage);
        Assert.Equal(existing.InsufficientEvidenceReasons, explicitPhase15.InsufficientEvidenceReasons);
    }

    private static QuestionAwareEvidenceSelectionContext Context() => new()
    {
        Enabled = true,
        InquiryText = Query,
        ProductName = "HelixQAC",
    };

    private static List<SearchSourceViewModel> RepresentativeItems() =>
    [
        Create("surrounding", "Validate製品の概要とダッシュボードの説明です。", 0.99, "OfficialDoc"),
        Create("command", "```qacli validate upload --qaf-project Demo --build-name Build01``` を実行します。", 0.55),
        Create("auth", "1. Validateへログインして認証tokenを取得します。\n2. projectを関連付けて接続します。", 0.52),
        Create("verify", "3. コマンドを実行します。完了後、Validate portalのBuild画面で結果を確認します。失敗時はerrorログを確認します。", 0.50),
    ];

    private static SearchSourceViewModel Create(
        string id, string text, double score, string sourceType = "Manual", string product = "HelixQAC") =>
        new(new SearchSource
        {
            SourceId = id,
            SourceType = sourceType,
            Title = id,
            Text = text,
            Score = score,
            ProductName = product,
        }, isSelected: false);
}
