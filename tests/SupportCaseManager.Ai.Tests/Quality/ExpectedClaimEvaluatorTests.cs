using SupportCaseManager.Ai.Core.Quality;

namespace SupportCaseManager.Ai.Tests.Quality;

public sealed class ExpectedClaimEvaluatorTests
{
    [Fact]
    public void Evaluate_DoesNotTreatOneOptionAsAllOptionsSatisfied()
    {
        var assessment = ExpectedClaimEvaluator.Evaluate(
            ["Azure SQL Database、Managed Instance、Windows VM上SQL Serverの可否を質問している。"],
            "Azure SQL Databaseの可否は未確認です。");
        Assert.Equal(3, assessment.Elements.Count);
        Assert.True(assessment.HasPartialMatch);
        Assert.False(assessment.AllElementsSatisfied);
        Assert.Equal(1d / 3, assessment.Coverage);
        Assert.Contains(assessment.Elements, item => item.MissingRequirements.Contains("Managed Instance"));
    }

    [Fact]
    public void Evaluate_RequiresIntentAndAttributedHistorySeparately()
    {
        const string claim = "Spring CORS検出を誤検知の可能性として相談した。案件履歴に9.7.7で修正との記録があるがメーカー原文未確認。";
        var partial = ExpectedClaimEvaluator.Evaluate([claim],
            "Spring CORSの指摘。案件履歴には9.7.7で修正との記録があります（メーカー原文未確認）。");
        Assert.False(partial.AllElementsSatisfied);
        Assert.Contains(partial.Elements, item => item.MissingRequirements.Contains("誤検知"));
        var complete = ExpectedClaimEvaluator.Evaluate([claim],
            "Spring CORS検出は誤検知の可能性として相談されています。案件履歴には9.7.7で修正との記録があります（メーカー原文未確認）。");
        Assert.True(complete.AllElementsSatisfied);
    }

    [Fact]
    public void Evaluate_DoesNotSplitVersionsAtDecimalPointsOrInventAttachmentContent()
    {
        var result = ExpectedClaimEvaluator.Evaluate(
            ["QAC 2026.1で同期設定が消失。添付画像に設定がある。復号XMLに別時点の設定がある。"],
            "QAC 2026.1で同期設定が消失しています。添付は未確認です。");
        Assert.Equal(3, result.Elements.Count);
        Assert.Equal(1d / 3, result.Coverage);
        Assert.False(result.AllElementsSatisfied);
    }

    [Theory]
    [InlineData("ログデータのバージョン情報がクライアント版を示すか未確認です。", false)]
    [InlineData("リリースノートには新機能が記載されています。", true)]
    [InlineData("Release NotesでVersion 9.9に新機能が追加されました。", true)]
    public void Extract_DistinguishesRetrievalIntentFromExplicitFeatureClaims(string answer, bool expected)
    {
        var claims = TechnicalClaimExtractor.Extract(answer, AnswerQualityEvaluator.CreateSupportCatalog("HelixQAC"));
        Assert.Equal(expected, claims.Any(item => item.Kind == "ProductFeature" && item.Value == "Release Notes"));
    }
}
