using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase22SelectorDeterminismTests
{
    [Fact]
    public void SameQuestionTwentyTimes_HasStableEvidenceOrderAndScores()
    {
        var executable = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return;
        }

        var items = new[]
        {
            Item("cct", 0.91, "QAC CCT compiler configuration generation condition"),
            Item("analysis", 0.83, "QAC project analysis qacli analyze procedure verification"),
            Item("upload", 0.74, "QAC Validate upload qacli validate build authentication"),
        };
        using var worker = new RustEvidenceSelectorWorkerClient();
        var snapshots = Enumerable.Range(0, 20).Select(_ =>
            SearchSourceSelectionBuilder.Build(items, 3, 0.1,
                questionAwareContext: new QuestionAwareEvidenceSelectionContext
                {
                    Enabled = true,
                    InquiryText = "QACでプロジェクトを解析するまでの手順とCCT自動生成条件を教えてください。",
                    ProductName = "HelixQAC",
                    UsePhase175QualityControls = true,
                    UseCoverageAwareEvidenceSelection = true,
                    CoverageAwareMaxEvidenceItems = 5,
                    MaxPromptChars = 12_000,
                    UseRustEvidenceSelector = true,
                    UsePersistentRustEvidenceSelector = true,
                    RustEvidenceSelectorWorkerClient = worker,
                    RustEvidenceSelectorExecutablePath = executable,
                    RustEvidenceSelectorTimeoutMs = 2_000,
                }))
            .Select(result => new
            {
                result.SelectorEngine,
                Evidence = result.Sources.Select(source => new { source.SourceId, source.Score }).ToArray(),
            })
            .ToList();

        var expected = snapshots[0];
        Assert.All(snapshots, actual =>
        {
            Assert.Equal("PersistentRust", actual.SelectorEngine);
            Assert.Equal(expected.Evidence.Select(item => item.SourceId), actual.Evidence.Select(item => item.SourceId));
            Assert.Equal(expected.Evidence.Select(item => item.Score), actual.Evidence.Select(item => item.Score));
        });
        Assert.Equal(20, worker.GetHealth().Requests);
    }

    private static SearchSourceViewModel Item(string id, double score, string text) => new(
        new SearchSource
        {
            SourceId = id,
            DocumentId = id,
            SourceType = "Manual",
            ProductName = "HelixQAC",
            Title = id,
            SectionTitle = id,
            Text = text,
            Score = score,
        },
        isSelected: true);
}
