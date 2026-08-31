using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.ViewModels;
using Xunit;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase50ConfigurationSelectionTests
{
    [Fact]
    public void ConfigurationQuestionKeepsConfigurationEvidenceAlongsideUploadEvidence()
    {
        var result = SearchSourceSelectionBuilder.Build(
            [
                Item("upload", 0.99, "qacli validate buildで解析結果をアップロードします。"),
                Item("configuration", 0.80, "Validateプロジェクトの設定手順です。プロジェクトを作成して設定します。"),
            ],
            maxEvidenceItems: 1,
            autoSelectMinimumScore: 0.10,
            questionAwareContext: new QuestionAwareEvidenceSelectionContext
            {
                Enabled = true,
                InquiryText = "Validateプロジェクトの設定手順を教えてください。",
                ProductName = "HelixQAC",
                UsePhase175QualityControls = true,
                UseCoverageAwareEvidenceSelection = true,
                CoverageAwareMaxEvidenceItems = 3,
                MaxPromptChars = 6000,
            });

        Assert.Contains(result.Sources, source => source.SourceId == "configuration");
        Assert.Contains("Configuration", result.RequiredCoverage);
        Assert.Contains("ProjectSetup", result.RequiredCoverage);
    }

    private static SearchSourceViewModel Item(string id, double score, string text) => new(
        new SearchSource
        {
            SourceId = id,
            SourceType = "Manual",
            ProductName = "HelixQAC",
            Title = id,
            Text = text,
            DocumentId = id,
            SectionTitle = id,
            Score = score,
        },
        isSelected: false);
}
