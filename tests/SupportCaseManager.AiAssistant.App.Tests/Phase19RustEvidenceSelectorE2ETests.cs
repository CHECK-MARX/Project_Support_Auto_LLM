using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class Phase19RustEvidenceSelectorE2ETests
{
    [Fact]
    public void E2eA_QacToValidateCli_RustMatchesCSharp()
    {
        var executable = RustExecutable();
        if (executable is null) return;
        const string question = "QAC Validate authentication connection project association upload command options build name verification incremental build troubleshooting";
        var items = new[]
        {
            Item("upload", 0.95, "QAC Validate upload command qacli validate build --build-name command options"),
            Item("auth", 0.90, "Validate authentication connection project association"),
            Item("incremental", 0.75, "Validate incremental build ibuild"),
            Item("verify", 0.70, "Validate verification troubleshooting error log"),
        };

        var csharp = Select(items, question, useRust: false, executable);
        var rust = Select(items, question, useRust: true, executable);

        Assert.Equal(csharp.Sources.Select(static item => item.SourceId), rust.Sources.Select(static item => item.SourceId));
        Assert.Equal(csharp.FinalCoverage, rust.FinalCoverage);
        Assert.Equal(csharp.MissingCoverage, rust.MissingCoverage);
        Assert.Equal("Rust", rust.SelectorEngine);
        Assert.Empty(rust.RustSelectorFallbackReason);
    }

    [Fact]
    public void E2eB_ValidateStream_RustMatchesCSharpWithoutExcludedTopics()
    {
        var executable = RustExecutable();
        if (executable is null) return;
        const string question = "Validate Stream overview purpose create configuration QAC association verification. Exclude license and IDE plugin.";
        var license = Item("license", 0.99, "Validate Stream license configuration");
        license.IsSelected = false;
        var ide = Item("ide", 0.98, "Validate Stream IDE plugin configuration");
        ide.IsSelected = false;
        var items = new[]
        {
            license,
            ide,
            Item("overview", 0.82, "Validate Stream overview purpose"),
            Item("creation", 0.78, "Validate Stream create configuration"),
            Item("association", 0.74, "Validate Stream QAC association verification"),
        };

        var csharp = Select(items, question, useRust: false, executable);
        var rust = Select(items, question, useRust: true, executable);

        Assert.Equal(csharp.Sources.Select(static item => item.SourceId), rust.Sources.Select(static item => item.SourceId));
        Assert.DoesNotContain(rust.Sources, static item => item.SourceId is "license" or "ide");
        Assert.Equal("Rust", rust.SelectorEngine);
    }

    [Fact]
    public void MissingRustExecutable_PreservesCSharpSelection()
    {
        var items = new[] { Item("a", 0.9, "QAC Validate upload command") };
        var context = Context("QAC Validate upload command", true,
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"));

        var result = SearchSourceSelectionBuilder.Build(items, 3, 0.1, questionAwareContext: context);

        Assert.Single(result.Sources);
        Assert.Equal("RustFallback", result.SelectorEngine);
        Assert.Equal("ExecutableMissing", result.RustSelectorFallbackReason);
    }

    private static SearchSourceSelectionResult Select(
        IReadOnlyList<SearchSourceViewModel> items,
        string question,
        bool useRust,
        string executable) => SearchSourceSelectionBuilder.Build(
            items,
            3,
            0.1,
            questionAwareContext: Context(question, useRust, executable));

    private static QuestionAwareEvidenceSelectionContext Context(string question, bool useRust, string executable) => new()
    {
        Enabled = true,
        InquiryText = question,
        ProductName = "HelixQAC",
        UsePhase175QualityControls = true,
        UseCoverageAwareEvidenceSelection = true,
        CoverageAwareMaxEvidenceItems = 5,
        MaxPromptChars = 12000,
        UseRustEvidenceSelector = useRust,
        RustEvidenceSelectorExecutablePath = executable,
        RustEvidenceSelectorTimeoutMs = 2000,
    };

    private static string? RustExecutable()
    {
        var path = Environment.GetEnvironmentVariable("RAG_SELECTOR_RS_EXE");
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

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
}
