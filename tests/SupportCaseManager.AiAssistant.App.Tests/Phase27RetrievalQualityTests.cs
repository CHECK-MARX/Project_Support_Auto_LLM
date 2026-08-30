using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.AiAssistant.App.ViewModels;
using Xunit;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase27RetrievalQualityTests
{
    [Fact]
    public void CoverageSelectionUsesBoundedExcerptBudgetForLongEvidence()
    {
        var result = SearchSourceSelectionBuilder.Build(
            [
                Item("procedure", 0.90, "QACプロジェクトを解析する手順。qacli analyze -P PROJECTで解析を実行します。" + new string('a', 900)),
                Item("verification", 0.80, "QACプロジェクトの解析結果を確認する手順。解析完了後に問題パネルを確認します。" + new string('b', 900)),
            ],
            maxEvidenceItems: 2,
            autoSelectMinimumScore: 0.10,
            questionAwareContext: new QuestionAwareEvidenceSelectionContext
            {
                Enabled = true,
                InquiryText = "QACでプロジェクトを解析する手順と解析結果の確認方法を教えてください。",
                ProductName = "HelixQAC",
                UsePhase175QualityControls = true,
                UseCoverageAwareEvidenceSelection = true,
                CoverageAwareMaxEvidenceItems = 5,
                MaxPromptChars = 1200,
            });

        Assert.Equal(2, result.Sources.Count);
        Assert.DoesNotContain("SelectionBudgetExceeded", result.InsufficientEvidenceReasons);
        Assert.True(result.EstimatedEvidenceChars <= 600);
    }

    private static SearchSourceViewModel Item(string id, double score, string text) => new(
        new SearchSource
        {
            SourceId = id,
            SourceType = "OfficialDoc",
            ProductName = "HelixQAC",
            Title = id,
            Text = text,
            DocumentId = id,
            SectionTitle = id,
            Score = score,
        },
        isSelected: true);
}
