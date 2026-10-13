using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class DevelopmentQualitySafetyTests
{
    [Theory]
    [InlineData("製品仕様上はツールが更新される可能性があります。")]
    [InlineData("製品仕様ではツールが更新されることが示されています。")]
    [InlineData("ツールの更新はサポートされています。")]
    [InlineData("ツールの更新はサポートされていますが、適用条件は未確認です。")]
    public void TokenOverlapDoesNotSupportUtilityUpdatePolicy(string answer)
    {
        var result = AnswerQualityEvaluator.Evaluate(new()
        {
            Question = "配布ユーティリティはQAC更新ごとに更新されますか。", Answer = answer,
            Evidence = [new() { SourceType = "Manual", Text = "QACプロジェクトをアップグレードできます。解析ツールチェーンを更新する。" }],
        });
        Assert.True(result.UnsupportedClaimCount > 0);
    }

    [Fact]
    public void UnknownUtilityPolicyIsNotAProductAssertion()
    {
        var result = AnswerQualityEvaluator.Evaluate(new()
        {
            Question = "配布ユーティリティは更新されますか。",
            Answer = "配布ユーティリティの更新可能性は提示資料では確認できません。メーカーの更新条件を確認してください。",
            Evidence = [],
        });
        Assert.Equal(0, result.UnsupportedClaimCount);
    }

    [Theory]
    [InlineData("顧客はAzure環境のバージョン9.8.2でEngineを構築しています。")]
    [InlineData("openFileの使用が禁止されているか確認してください。")]
    public void PastCaseValuesDoNotDescribeCurrentCustomer(string answer)
    {
        var result = AnswerQualityEvaluator.Evaluate(new()
        {
            Question = "Customerポータルへログインできず、メールが見つかりません。", Answer = answer,
            Evidence = [new() { SourceType = "PastCaseNote", Text = "Azure環境のバージョン9.8.2でEngineを構築。openFileの確認。" }],
        });
        Assert.Contains(result.UnsupportedTechnicalClaims, claim => claim.Kind == "CaseAttribution");
    }

    [Fact]
    public void AttributedPastCaseRemainsAuxiliaryNotCurrentObservation()
    {
        var result = AnswerQualityEvaluator.Evaluate(new()
        {
            Question = "ログインを確認したい。", Answer = "過去案件ではAzure環境での構築が記録されています。",
            Evidence = [new() { SourceType = "PastCaseNote", Text = "Azure環境で構築。" }],
        });
        Assert.DoesNotContain(result.UnsupportedTechnicalClaims, claim => claim.Kind == "CaseAttribution");
    }

    [Fact]
    public void UnsupportedConditionalSupportCannotDropVersionAndTiming()
    {
        var result = AnswerQualityEvaluator.Evaluate(new()
        {
            Question = "Azureへの移行を検討します。", Answer = "Azureはサポートされています。",
            Evidence = [new() { SourceType = "OfficialDoc", Text = "Azure is supported from version 9.8 if configuration is verified before migration." }],
        });
        Assert.True(result.UnsupportedClaimCount > 0);
    }

    [Fact]
    public void GroundedPostprocessingKeepsExactQuoteDespiteLegacyAttachmentFallback()
    {
        const string quote = "署名を確認してください。添付手順では署名を照合します。";
        var source = new SearchSource { SourceId = "synthetic:one", SourceType = "PastCaseNote",
            Score = 0.9, Text = "先頭の別情報。\n" + quote + "\n末尾の別情報。" };
        var item = new EvidenceItem { SourceId = source.SourceId, SourceType = source.SourceType, Excerpt = quote, Relevance = 0.9 };
        var result = AnswerPostProcessor.Process(new AnswerDraftRequest
        {
            InquiryText = "署名を確認したい。", Sources = [source], Case = new(), Settings = new(),
        }, new AnswerDraftResult { AnswerGenerationMode = AnswerGenerationModes.GroundedCandidate,
            CustomerReplyDraft = "署名を確認してください。", Evidence = [item] }, [item], 0.5, []);
        Assert.Equal(quote, Assert.Single(result.Evidence).Excerpt);
        Assert.Equal(source.SourceId, result.Evidence[0].SourceId);
    }

    [Theory]
    [InlineData("Boxに添付があります。", "別顧客のAzure構築。添付はBoxにあります。")]
    [InlineData("Eclipseプラグインを確認します。", "ライセンスサーバーを移行します。")]
    public void GenericMailWordsDoNotBindForeignEvidence(string inquiry, string text)
    {
        Assert.False(AnswerQualityEvaluator.SourceMatchesInquiry(inquiry, text, "PastCaseNote"));
        var source = new SearchSource { SourceId = "synthetic:foreign", SourceType = "PastCaseNote", Text = text };
        var prompt = GroundedAnswerPromptBuilder.Build(new()
        {
            InquiryText = inquiry, Case = new(), Settings = new() { MaxPromptChars = 8000 },
            FactResolution = new() { AnswerReadiness = "InsufficientEvidence" },
        }, [source]);
        Assert.DoesNotContain(text, prompt.UserPrompt);
        Assert.Equal(0, prompt.OutputSchema!.Value.GetProperty("properties").GetProperty("evidence").GetProperty("maxItems").GetInt32());
    }

    [Theory]
    [InlineData("顧客の添付ログにはReceiverのMQ接続失敗が記録されています。", true)]
    [InlineData("顧客のログにはReceiverのMQ接続失敗が記録されています。", true)]
    [InlineData("過去案件のログにはReceiverのMQ接続失敗が記録されています。", false)]
    [InlineData("ログには別ComponentのMQ接続失敗が記録されています。", false)]
    [InlineData("ログにはReceiverのMQ接続成功が記録されています。", false)]
    public void LogAttributionUsesMeaningWithoutLosingComponentOrPolarity(string reply, bool valid)
    {
        var facts = new FactResolutionResult { ResolvedFacts = [new() { Key = "ObservedLogFailure",
            Status = "Candidate", SourceType = "CurrentCase", EvidenceId = "case:log:synthetic",
            Value = "添付ログにReceiverのMQ接続失敗が記録されている。" }] };
        Assert.Equal(valid, ImportantFactContract.Validate(facts, "確認できる事実: " + reply) is null);
    }

    [Fact]
    public void InquiryVersionAndAccountOptionsSurviveEvenWithoutFocusOrBaseline()
    {
        var facts = ImportantFactContract.Enrich(new(), "対象版9.8.4 HF6で個人アカウントか専用アカウントを使用できますか。",
            null, new SafetyRedactionService());
        Assert.Contains(facts.ResolvedFacts, fact => fact.Key == "ImportantInquiryVersion" && fact.Value == "9.8.4 HF6");
        Assert.Equal(2, facts.ResolvedFacts.Count(fact => fact.Key == "ImportantInquiryOption"));
        Assert.NotNull(ImportantFactContract.Validate(facts, "個人アカウントか専用アカウントの可否を確認します。"));
        Assert.Null(ImportantFactContract.Validate(facts, "9.8.4 HF6で個人アカウントと専用アカウントの可否を確認します。"));
    }
}
