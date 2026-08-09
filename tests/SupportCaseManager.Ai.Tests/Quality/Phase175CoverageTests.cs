using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Quality;

namespace SupportCaseManager.Ai.Tests.Quality;

public sealed class Phase175CoverageTests
{
    private const string Question = "Perforce QACの解析結果をValidateへアップロードする一連のCLI手順、オプション、認証、接続、確認、失敗時の確認事項を教えてください。";
    private const string FullEvidence = """
        qacli authで認証します。qacli validate connect --url https://validate.example で接続し、
        qacli validate config --project Demoで設定します。qacli validate build --qaf-project . --build-name Build01でアップロードします。
        Validate portalのBuild一覧で表示を確認します。失敗時はエラーログと認証、接続設定を確認します。
        """;

    [Fact]
    public void Evaluate_FullEvidenceButIncompleteAnswer_IsNeedsReview()
    {
        var result = Evaluate(
            "qacli validate build --qaf-project . --build-name Build01を実行してアップロードします。",
            FullEvidence);

        Assert.Equal(1, result.EvidenceCoverage);
        Assert.True(result.AnswerCoverage < 1);
        Assert.Contains(CoverageAnalyzer.Authentication, result.MissingAnswerCoverage!);
        Assert.Contains(CoverageAnalyzer.Connection, result.MissingAnswerCoverage!);
        Assert.Equal(AnswerQualityDecisions.NeedsReview, result.Decision);
    }

    [Fact]
    public void Evaluate_MissingEvidenceCoverage_IsInsufficientEvidence()
    {
        const string partial = "qacli validate build --qaf-project .でアップロードします。";
        var result = Evaluate(partial, partial);

        Assert.True(result.EvidenceCoverage < 1);
        Assert.NotEmpty(result.MissingEvidenceCoverage!);
        Assert.Equal(AnswerQualityDecisions.InsufficientEvidence, result.Decision);
    }

    [Fact]
    public void Evaluate_FullEvidenceAndAnswer_IsCustomerReadyWhenOtherChecksPass()
    {
        var result = Evaluate(FullEvidence, FullEvidence);

        Assert.Equal(1, result.EvidenceCoverage);
        Assert.Equal(1, result.AnswerCoverage);
        Assert.Empty(result.MissingEvidenceCoverage!);
        Assert.Empty(result.MissingAnswerCoverage!);
        Assert.Equal(AnswerQualityDecisions.CustomerReady, result.Decision);
    }

    private static AnswerQualityEvaluationResult Evaluate(string answer, string evidence) =>
        AnswerQualityEvaluator.Evaluate(new AnswerQualityEvaluationInput
        {
            Question = Question,
            Answer = answer,
            ProductName = "HelixQAC",
            Evidence =
            [
                new AnswerQualityEvidence
                {
                    SourceId = "synthetic-manual",
                    SourceType = "Manual",
                    Text = evidence,
                    ProductName = "HelixQAC",
                },
            ],
            Catalog = AnswerQualityEvaluator.CreateSupportCatalog("HelixQAC"),
            UseSeparatedCoverage = true,
        });
}
