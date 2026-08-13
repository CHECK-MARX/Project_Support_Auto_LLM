using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase21PersistentRustWorkerWpfE2ETests
{
    [Fact]
    public void ArtificialWpfQueries_UseOnePersistentWorkerAndMatchCSharp()
    {
        var executable = RustExecutable();
        if (executable is null)
        {
            return;
        }

        using var worker = new RustEvidenceSelectorWorkerClient();
        var cases = new[]
        {
            new TestCase(
                "QACのインストール方法を教えてください。",
                [
                    Item("install", 0.92, "Helix QAC install setup installer prerequisite procedure verification"),
                    Item("license", 0.60, "Helix QAC license server configuration"),
                    Item("validate", 0.40, "Validate upload command"),
                ]),
            new TestCase(
                "QACの解析結果をValidateへCLIでアップロードする手順を教えてください。",
                [
                    Item("upload", 0.95, "QAC Validate upload CLI qacli validate build command options"),
                    Item("auth", 0.86, "Validate authentication connection project association"),
                    Item("build", 0.75, "Validate build name verification troubleshooting"),
                ]),
            new TestCase(
                "Validate Streamの作成、QACとの関連付け、確認手順を教えてください。",
                [
                    Item("stream", 0.91, "Validate Stream create configuration overview"),
                    Item("association", 0.84, "Validate Stream QAC project association procedure"),
                    Item("verify", 0.76, "Validate Stream verification status"),
                ]),
        };

        int? processId = null;
        foreach (var testCase in cases)
        {
            var persistent = Select(testCase, executable, worker, persistent: true);
            var csharp = Select(testCase, executable, worker, persistent: false);

            Assert.Equal("PersistentRust", persistent.SelectorEngine);
            Assert.Empty(persistent.RustSelectorFallbackReason);
            Assert.Equal(
                csharp.Sources.Select(static item => item.SourceId),
                persistent.Sources.Select(static item => item.SourceId));
            Assert.NotNull(persistent.PersistentRustWorkerHealth);
            processId ??= persistent.PersistentRustWorkerHealth.ProcessId;
            Assert.Equal(processId, persistent.PersistentRustWorkerHealth.ProcessId);
        }

        Assert.Equal(3, worker.GetHealth().Requests);
        Assert.Equal(0, worker.GetHealth().Restarts);
    }

    private static SearchSourceSelectionResult Select(
        TestCase testCase,
        string executable,
        IRustEvidenceSelectorWorkerClient worker,
        bool persistent) => SearchSourceSelectionBuilder.Build(
            testCase.Items,
            3,
            0.1,
            questionAwareContext: new QuestionAwareEvidenceSelectionContext
            {
                Enabled = true,
                InquiryText = testCase.Question,
                ProductName = "HelixQAC",
                UsePhase175QualityControls = true,
                UseCoverageAwareEvidenceSelection = true,
                CoverageAwareMaxEvidenceItems = 5,
                MaxPromptChars = 12_000,
                UseRustEvidenceSelector = persistent,
                UsePersistentRustEvidenceSelector = persistent,
                RustEvidenceSelectorWorkerClient = worker,
                RustEvidenceSelectorExecutablePath = executable,
                RustEvidenceSelectorTimeoutMs = 2_000,
            });

    private static SearchSourceViewModel Item(string id, double score, string text) => new(
        new SearchSource
        {
            SourceId = id,
            SourceType = "Manual",
            ProductName = "HelixQAC",
            Title = id,
            Text = text,
            FilePath = $@"C:\artificial\{id}.txt",
            DocumentId = id,
            SectionTitle = id,
            Score = score,
        },
        isSelected: true);

    private static string? RustExecutable()
    {
        var path = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE");
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    private sealed record TestCase(string Question, IReadOnlyList<SearchSourceViewModel> Items);
}
