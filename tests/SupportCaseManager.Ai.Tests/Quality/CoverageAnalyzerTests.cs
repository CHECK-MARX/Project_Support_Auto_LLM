using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Tests.Quality;

public sealed class CoverageAnalyzerTests
{
    [Fact]
    public void ObserveForCoverageSelection_DoesNotTreatDeletingResultsAsVerification()
    {
        var observed = CoverageAnalyzer.ObserveForCoverageSelection(
            "QACプロジェクトの解析結果を削除し、qacli analyze を再実行します。");

        Assert.DoesNotContain(CoverageAnalyzer.AnalysisVerification, observed);
    }

    [Fact]
    public void ObserveForCoverageSelection_DoesNotJoinDistantGenericConfirmationToAnalysisResult()
    {
        var observed = CoverageAnalyzer.ObserveForCoverageSelection(
            "解析結果をCSVへ出力します。" + new string('x', 120) + "設定内容を確認します。");

        Assert.DoesNotContain(CoverageAnalyzer.AnalysisVerification, observed);
    }

    [Theory]
    [InlineData("QACプロジェクトの解析中は解析ダイアログで進捗を確認します。")]
    [InlineData("QACプロジェクトの解析結果を問題ビューで確認します。")]
    [InlineData("Review the analysis result in the analysis dialog.")]
    public void ObserveForCoverageSelection_RecognizesExplicitVerification(string text)
    {
        var observed = CoverageAnalyzer.ObserveForCoverageSelection(text);

        Assert.Contains(CoverageAnalyzer.AnalysisVerification, observed);
    }

    [Fact]
    public void RequiredForCoverageSelection_RequiresBothRequestedUploadInterfaces()
    {
        const string question =
            "QACで解析した結果をValidateへアップロードする方法を教えてください。GUIとCLIの方法を教えてください。";
        var profile = TopicEntityAnalyzer.Extract(question, SupportTopicCatalog.Create("HelixQAC"));

        var required = CoverageAnalyzer.RequiredForCoverageSelection(question, profile);

        Assert.Contains(CoverageAnalyzer.GuiUploadProcedure, required);
        Assert.Contains(CoverageAnalyzer.CliUploadProcedure, required);
    }

    [Theory]
    [InlineData("ポータル > Validate > 解析結果をアップロード", CoverageAnalyzer.GuiUploadProcedure)]
    [InlineData("qacli validate build --qaf-project sample", CoverageAnalyzer.CliUploadProcedure)]
    public void ObserveForCoverageSelection_SeparatesValidateGuiAndCliProcedures(
        string text,
        string expectedCoverage)
    {
        var observed = CoverageAnalyzer.ObserveForCoverageSelection(text);

        Assert.Contains(expectedCoverage, observed);
    }
}
