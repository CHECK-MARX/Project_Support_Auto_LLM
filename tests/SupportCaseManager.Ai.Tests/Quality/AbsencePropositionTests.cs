using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Quality;

namespace SupportCaseManager.Ai.Tests.Quality;

public sealed class AbsencePropositionTests
{
    [Theory]
    [InlineData("公式資料にFeatureAlphaの手順は記載されていません。")]
    [InlineData("現時点で断定できない事項: FeatureAlphaの手順が公式資料に明記されていないこと。")]
    [InlineData("公式仕様でFeatureAlphaは定義されていません。")]
    [InlineData("FeatureAlphaはサポートされていません。")]
    [InlineData("FeatureAlphaはサポート対象外です。")]
    [InlineData("マニュアルにはFeatureAlphaの手順が存在しません。")]
    [InlineData("今回確認した資料にはFeatureAlphaの手順の記載がありません。")]
    [InlineData("FeatureAlpha is not documented in the manual.")]
    [InlineData("FeatureAlpha is not defined in the specification.")]
    [InlineData("FeatureAlpha is not supported.")]
    public void TokenOverlapOrUnrelatedDocumentationDoesNotProveAbsence(string answer)
    {
        var result = Evaluate(answer, "FeatureAlphaの設定を確認してください。FeatureBetaはサポートされていません。");
        Assert.Contains(result.UnsupportedTechnicalClaims, claim => claim.Kind == "EvidenceAbsence");
        Assert.Contains("MajorUnsupportedTechnicalClaim", result.BlockingReasons);
    }

    [Theory]
    [InlineData("今回確認できた資料からはFeatureAlphaの手順を確認できません。")]
    [InlineData("提示された資料の範囲ではFeatureAlphaのサポート可否は未確認です。")]
    [InlineData("FeatureAlphaが公式資料に記載されていないとは断定できません。")]
    [InlineData("FeatureAlphaがサポートされていないかどうかをメーカーへ確認してください。")]
    public void ScopedUncertaintyIsNotAnAbsenceAssertion(string answer)
    {
        Assert.DoesNotContain(Evaluate(answer).UnsupportedTechnicalClaims, claim => claim.Kind == "EvidenceAbsence");
    }

    [Fact]
    public void UncertaintyInNextSentenceDoesNotExcuseDefiniteAbsence()
    {
        var result = Evaluate("FeatureAlphaの手順は公式資料に明記されていません。適用条件は確認できません。");
        Assert.Contains(result.UnsupportedTechnicalClaims, claim => claim.Kind == "EvidenceAbsence");
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("OfficialDoc")]
    [InlineData("Manufacturer")]
    public void ExplicitPrimarySourceCanSupportBoundNegativeFact(string sourceType)
    {
        var result = Evaluate("FeatureAlphaはサポートされていません。", "FeatureAlphaはサポートされていません。", sourceType);
        Assert.Equal(0, result.UnsupportedClaimCount);
    }

    [Theory]
    [InlineData("PastCaseNote")]
    [InlineData("PastAnswer")]
    [InlineData("CurrentCase")]
    public void AuxiliaryOrCaseSourceDoesNotProveProductAbsence(string sourceType)
    {
        var result = Evaluate("FeatureAlphaはサポートされていません。", "FeatureAlphaはサポートされていません。", sourceType);
        Assert.Contains(result.UnsupportedTechnicalClaims, claim => claim.Kind == "EvidenceAbsence");
    }

    [Theory]
    [InlineData("FeatureAlphaの手順は公式資料に明記されていません。", "FeatureAlphaの手順は記載されていますが、FeatureBetaの手順は公式資料に明記されていません。")]
    [InlineData("FeatureAlphaはサポートされていません。", "FeatureAlphaはサポートされていますが、FeatureBetaはサポートされていません。")]
    [InlineData("FeatureAlphaはサポートされていません。", "FeatureBetaはサポートされていませんが、FeatureAlphaはサポートされています。")]
    [InlineData("FeatureAlphaはサポートされていません。", "FeatureAlphaはバージョン9.8の場合に限りサポートされていません。")]
    [InlineData("FeatureAlphaは定義されていません。", "公式資料にはFeatureAlphaの手順が記載されていません。")]
    public void ProofMustBindSameSubjectPredicateVersionAndCondition(string answer, string source)
    {
        Assert.Contains(Evaluate(answer, source).UnsupportedTechnicalClaims, claim => claim.Kind == "EvidenceAbsence");
    }

    [Fact]
    public void ExplicitDocumentAbsenceRequiresItsOwnStatement()
    {
        const string fact = "公式資料にはFeatureAlphaの手順が明記されていません。";
        Assert.DoesNotContain(Evaluate(fact, fact).UnsupportedTechnicalClaims, claim => claim.Kind == "EvidenceAbsence");
    }

    [Fact]
    public void ExplicitConditionalNegativeRetainsItsVersionAndCondition()
    {
        const string fact = "FeatureAlphaはバージョン9.8の場合に限りサポートされていません。";
        Assert.Equal(0, Evaluate(fact, fact).UnsupportedClaimCount);
    }

    private static AnswerQualityEvaluationResult Evaluate(string answer, string? source = null, string type = "OfficialDoc") =>
        AnswerQualityEvaluator.Evaluate(new()
        {
            Question = "FeatureAlphaの仕様とサポート条件を確認したい。", Answer = answer,
            Evidence = source is null ? [] : [new() { SourceType = type, Text = source }],
        });
}
