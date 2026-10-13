using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Quality;

namespace SupportCaseManager.Ai.Tests.Quality;

public sealed class RelationshipPropositionTests
{
    [Theory]
    [InlineData("更新がログイン失敗の原因です。")]
    [InlineData("更新によってアクセス権限が変更されました。")]
    [InlineData("更新により、アクセス権限が変更されている可能性があり、それがログイン失敗の原因となる可能性があります。")]
    [InlineData("更新のため再設定が必要になりました。")]
    [InlineData("入力ミスにより通知が届かない可能性があります。")]
    [InlineData("AlphaによってBetaの応答が遅延します。")]
    [InlineData("Engineの起動にはPortalの起動が必要です。")]
    [InlineData("Engineの起動はPortalの起動に依存します。")]
    [InlineData("Upgrade may cause login failure.")]
    [InlineData("Upgrade causes login failure.")]
    [InlineData("Engine startup requires Portal startup.")]
    [InlineData("権限変更がログイン失敗に関与している可能性があります。")]
    public void CooccurringEndpointsDoNotEstablishDirectedRelationship(string answer)
    {
        AssertRelationshipRejected(answer, "更新を実施しました。アクセス権限を確認します。ログイン失敗を報告しました。Engineの起動。Portalの起動。Upgrade. login failure. Engine startup. Portal startup.");
    }

    [Theory]
    [InlineData("更新がログイン失敗の原因です。", "ログイン失敗が更新の原因です。")]
    [InlineData("Engineの起動はPortalの起動に依存します。", "Portalの起動はEngineの起動に依存します。")]
    [InlineData("9.8への更新によりPortalの権限が変更されます。", "9.7への更新によりPortalの権限が変更されます。")]
    [InlineData("9.8への更新によりPortalの権限が変更されます。", "9.8への更新によりEngineの権限が変更されます。")]
    [InlineData("移設によりPortalの権限が変更されます。", "再起動によりPortalの権限が変更されます。")]
    [InlineData("更新がログイン失敗の原因です。", "更新がログイン失敗の原因ではありません。")]
    [InlineData("更新がログイン失敗の原因です。", "更新がログイン失敗の原因となる可能性があります。")]
    [InlineData("更新がログイン失敗の原因です。", "特定の設定の場合、更新がログイン失敗の原因です。")]
    [InlineData("更新と権限変更がログイン失敗の原因です。", "更新がログイン失敗の原因です。")]
    public void DirectionRolesPolarityModalityAndConditionsCannotBeBorrowed(string answer, string source)
    {
        AssertRelationshipRejected(answer, source);
    }

    [Fact]
    public void CannotAssembleCausalChainAcrossSpansOrSources()
    {
        var result = Evaluate("更新がログイン失敗の原因です。", [
            Evidence("更新が権限変更の原因です。"), Evidence("権限変更がログイン失敗の原因です。")]);
        Assert.Contains(result.UnsupportedTechnicalClaims, x => x.Kind == "EvidenceRelationship");
    }

    [Theory]
    [InlineData("更新によってアクセス権限が変更されました。")]
    [InlineData("更新がログイン失敗の原因です。")]
    [InlineData("Engineの起動にはPortalの起動が必要です。")]
    [InlineData("Engineの起動はPortalの起動に依存します。")]
    [InlineData("9.8への更新によりPortalの権限が変更されます。")]
    [InlineData("Upgrade causes login failure.")]
    [InlineData("Engine startup requires Portal startup.")]
    public void DirectPrimaryRelationSupportsSameBoundClaim(string fact)
    {
        Assert.DoesNotContain(Evaluate(fact, [Evidence(fact)]).UnsupportedTechnicalClaims, x => x.Kind == "EvidenceRelationship");
    }

    [Theory]
    [InlineData("提示資料では更新とログイン失敗の因果関係は確認できません。")]
    [InlineData("更新がログイン失敗の原因かどうかを確認してください。")]
    [InlineData("ログイン失敗の原因を特定するため、エラーメッセージをご提供ください。")]
    [InlineData("ログイン失敗の原因を確認するため追加情報が必要です。")]
    [InlineData("更新に伴う設定変更が必要かどうか、および現在の版と目標版を明示してください。")]
    [InlineData("選択肢の正否を明確にするため、公式資料が必要です。")]
    [InlineData("エラーメッセージの内容により、設定ミスが原因であるかを特定するための情報が必要です。")]
    [InlineData("更新がログイン失敗の原因となる可能性がありますか？")]
    public void UnknownRelationOrInformationRequestDoesNotAssertProductDependency(string answer)
    {
        Assert.DoesNotContain(Evaluate(answer, []).UnsupportedTechnicalClaims, x => x.Kind == "EvidenceRelationship");
    }

    [Fact]
    public void UnknownHeadingDoesNotExcuseTentativeCausalAssertion()
    {
        AssertRelationshipRejected("現時点で断定できない事項: 更新によりアクセス権限が変更された可能性があります。", "更新と権限を確認する。");
    }

    [Fact]
    public void UnrelatedQuestionCannotHideAffirmativeRelationInSameSentence()
    {
        AssertRelationshipRejected("更新がログイン失敗の原因です、また設定値が正しいかどうか確認してください。", "設定値を確認する。");
    }

    [Fact]
    public void AttributedCurrentCaseRelationCannotBecomeProductSpecification()
    {
        const string fact = "更新がログイン失敗の原因です。";
        var evidence = new[] { Evidence(fact, "CurrentCase") };
        Assert.DoesNotContain(Evaluate("顧客の申告では更新がログイン失敗の原因です。", evidence).UnsupportedTechnicalClaims, x => x.Kind == "EvidenceRelationship");
        Assert.Contains(Evaluate("製品仕様上、更新がログイン失敗の原因です。", evidence).UnsupportedTechnicalClaims, x => x.Kind == "EvidenceRelationship");
        Assert.Contains(Evaluate("更新がログイン失敗の原因です。", evidence).UnsupportedTechnicalClaims, x => x.Kind == "EvidenceRelationship");
    }

    [Fact]
    public void TemporalCurrentCaseObservationIsNotCausalProof()
    {
        Assert.Contains(Evaluate("顧客の申告では更新がログイン失敗の原因です。", [Evidence("更新後にログイン失敗を観測しました。", "CurrentCase")]).UnsupportedTechnicalClaims, x => x.Kind == "EvidenceRelationship");
    }

    [Fact]
    public void TentativeQuestionDoesNotSupportAttributedCausalClaim()
    {
        Assert.Contains(Evaluate("顧客の申告では更新がログイン失敗の原因となる可能性があります。",
            [Evidence("更新がログイン失敗の原因となる可能性がありますか？", "CurrentCase")]).UnsupportedTechnicalClaims, x => x.Kind == "EvidenceRelationship");
    }

    [Fact]
    public void CurrentCaseRequiresProvenanceAndUnverifiedManufacturerAttribution()
    {
        var fact = Evidence("案件履歴では更新がログイン失敗の原因です。メーカー原文未確認。", "CurrentCase");
        Assert.Contains(Evaluate("案件履歴では更新がログイン失敗の原因です。", [fact]).UnsupportedTechnicalClaims, x => x.Kind == "EvidenceRelationship");
        Assert.Contains(Evaluate("顧客の申告では更新がログイン失敗の原因です。", [fact with { SourceId = "" }]).UnsupportedTechnicalClaims, x => x.Kind == "EvidenceRelationship");
    }

    private static void AssertRelationshipRejected(string answer, string source)
    {
        var result = Evaluate(answer, [Evidence(source)]);
        Assert.Contains(result.UnsupportedTechnicalClaims, x => x.Kind == "EvidenceRelationship");
        Assert.Equal(AnswerQualityDecisions.Blocked, result.Decision);
    }

    private static AnswerQualityEvidence Evidence(string text, string type = "OfficialDoc") => new() { SourceId = "source:original", SourceType = type, Text = text };
    private static AnswerQualityEvaluationResult Evaluate(string answer, IReadOnlyList<AnswerQualityEvidence> evidence) =>
        AnswerQualityEvaluator.Evaluate(new() { Question = "更新時の問題と依存条件を確認したい。", Answer = answer, Evidence = evidence });
}
