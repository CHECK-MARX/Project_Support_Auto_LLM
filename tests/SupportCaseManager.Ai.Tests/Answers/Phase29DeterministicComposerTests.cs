using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using Xunit;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class Phase29DeterministicComposerTests
{
    [Fact]
    public void AnalysisCliUsesCompleteEvidenceCommandAndStructuredSections()
    {
        var request = Request("QACの解析CLIコマンドとオプションを教えてください。", [
            Source("manual", "qacli analyze -P <project-directory> --build-command <script>。-P は解析対象プロジェクトを指定します。"),
            Source("past", "qacli analyze --secret-customer-option を使ってください。") with { SourceType = "PastCase" }
        ]);

        Assert.True(HowToAnswerComposer.TryComposeAnalysisCli(request, out var answer));
        Assert.Contains("`qacli analyze -P <project-directory> --build-command <script>`", answer, StringComparison.Ordinal);
        Assert.Contains("【CLIコマンド】", answer, StringComparison.Ordinal);
        Assert.Contains("-P は解析対象プロジェクトを指定します。", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-customer-option", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalysisCliDoesNotJoinCommandFragmentsOrInventOptions()
    {
        var request = Request("QACの解析CLIコマンドを教えてください。", [
            Source("prefix", "qacli analyze"),
            Source("option", "-P <project-directory> --raw-source")
        ]);

        Assert.False(HowToAnswerComposer.TryComposeAnalysisCli(request, out _));
    }

    [Fact]
    public void AnalysisCliDoesNotUseValidateCommandAsAnalysisCommand()
    {
        var request = Request("QACの解析CLIコマンドを教えてください。", [
            Source("validate", "qacli validate build -P <project-directory>")
        ]);

        Assert.False(HowToAnswerComposer.TryComposeAnalysisCli(request, out _));
    }

    [Fact]
    public void FailureFallbackUsesAnalysisCliComposer()
    {
        var request = Request("QACの解析CLIコマンドを教えてください。", [
            Source("manual", "qacli analyze -P <project-directory> --build-command <script>。")
        ]);

        var result = AnswerPostProcessor.BuildFailureFallback(request, new TimeoutException());

        Assert.Contains("【CLIコマンド】", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("`qacli analyze -P <project-directory> --build-command <script>`", result.CustomerReplyDraft, StringComparison.Ordinal);
    }

    private static AnswerDraftRequest Request(string inquiry, IReadOnlyList<SearchSource> sources) => new()
    {
        Case = new CaseContext { ProductName = "HelixQAC" },
        InquiryText = inquiry,
        Sources = sources,
        Settings = new AiAssistantSettings { MaxEvidenceItems = 5 }
    };

    private static SearchSource Source(string id, string text) => new()
    {
        SourceId = id,
        SourceType = "Manual",
        Title = "QAC Manual",
        DocumentTitle = "QAC Manual",
        Text = text,
        Score = 0.9
    };
}
