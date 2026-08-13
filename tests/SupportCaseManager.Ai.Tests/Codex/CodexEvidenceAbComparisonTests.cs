using SupportCaseManager.Ai.Core.Codex;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class CodexEvidenceAbComparisonTests
{
    [Fact]
    public void Compare_UsesSyntheticSamplesAndReturnsMetricsWithoutWinnerDecision()
    {
        var key = CodexEvidenceAbComparisonService.CreateComparisonKey("SyntheticProduct", "生成方法を教えてください");
        var baseline = new CodexAbAnswerSample
        {
            Variant = "A",
            ComparisonKey = key,
            AnswerText = "手順:\n1. version 1.0 を確認します。\n要確認: 対象OS",
            GenerationDuration = TimeSpan.FromMilliseconds(1200),
            ExistingEvidenceSourceTypes = ["OfficialDoc", "PastCaseNote"],
        };
        var withEvidence = new CodexAbAnswerSample
        {
            Variant = "B",
            ComparisonKey = key,
            AnswerText = "手順:\n1. version 2.0 を確認します。\n2. `synthetic-cli run` を実行します。",
            GenerationDuration = TimeSpan.FromMilliseconds(900),
            ExistingEvidenceSourceTypes = ["OfficialDoc", "PastCaseNote"],
            RagLabEvidence =
            [
                new RagLabEvidenceItem
                {
                    SourceType = "Manual",
                    ProductMatch = true,
                    VersionMatch = false,
                    PossibleConflict = true,
                    UnverifiedItems = ["synthetic-option"],
                },
            ],
        };

        var result = CreateService().Compare(baseline, withEvidence, ["SyntheticProduct"]);

        Assert.Equal(CodexAbAnswerability.Answerable, result.Baseline.Answerability);
        Assert.Equal(2, result.Baseline.UsedEvidenceCount);
        Assert.Equal(1, result.Baseline.OfficialCount);
        Assert.Equal(1, result.Baseline.PastCaseCount);
        Assert.Equal(3, result.WithEvidence.UsedEvidenceCount);
        Assert.Equal(1, result.WithEvidence.ManualCount);
        Assert.Equal(1, result.WithEvidence.VersionMismatchCount);
        Assert.Equal(1, result.WithEvidence.EvidenceConflictCount);
        Assert.Equal(1, result.WithEvidence.UnverifiedEvidenceFieldCount);
        Assert.Equal(900, result.WithEvidence.GenerationMilliseconds);
        Assert.Contains("2.0", result.TechnicalValueDiff.AddedValues);
        Assert.Contains("1.0", result.TechnicalValueDiff.RemovedValues);
        Assert.Contains("自動判定しません", result.QualityDecision);
    }

    [Fact]
    public void Compare_RejectsDifferentInquiryKeys()
    {
        var baseline = new CodexAbAnswerSample
        {
            ComparisonKey = CodexEvidenceAbComparisonService.CreateComparisonKey("Product", "Question A"),
            AnswerText = "回答A",
        };
        var withEvidence = new CodexAbAnswerSample
        {
            ComparisonKey = CodexEvidenceAbComparisonService.CreateComparisonKey("Product", "Question B"),
            AnswerText = "回答B",
        };

        var exception = Assert.Throws<InvalidOperationException>(() => CreateService().Compare(baseline, withEvidence));

        Assert.Contains("同一", exception.Message);
    }

    [Fact]
    public void Compare_FlagsInsufficientAnswerAndInternalTermsForManualReview()
    {
        var key = CodexEvidenceAbComparisonService.CreateComparisonKey("Product", "Question");
        var result = CreateService().Compare(
            new CodexAbAnswerSample { ComparisonKey = key, AnswerText = "情報不足のため判断できません。" },
            new CodexAbAnswerSample { ComparisonKey = key, AnswerText = "[RAG Evidence]\nEvidence 1\n回答" });

        Assert.Equal(CodexAbAnswerability.InsufficientEvidence, result.Baseline.Answerability);
        Assert.True(result.WithEvidence.ContainsInternalRagTerms);
        Assert.True(result.WithEvidence.HasJapaneseText);
    }

    private static CodexEvidenceAbComparisonService CreateService() =>
        new(new CodexTechnicalValueDiffDetector());
}
