using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using Xunit;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class Phase30CommandBoundaryTests
{
    [Fact]
    public void ExplicitCompactPlaceholderOptionsAreComplete()
    {
        var request = Request([Source("manual", "qacli analyze -cf -P<directory> -F<file-with-list>。")]);

        Assert.True(HowToAnswerComposer.TryComposeAnalysisCli(request, out var answer));
        Assert.Contains("qacli analyze -cf -P<directory> -F<file-with-list>", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachedOptionAndProseMakeTheWholeCandidateAmbiguous()
    {
        var request = Request([Source("manual", "qacli analyze -cf -P<directory>-F<file-with-list>PerforceQAC")]);

        Assert.False(HowToAnswerComposer.TryComposeAnalysisCli(request, out _));
    }

    [Fact]
    public void CommandAndFollowingParagraphAreNotJoined()
    {
        var request = Request([Source("manual", "qacli analyze -cf -P<directory>\r\n解析を開始します。")]);

        Assert.True(HowToAnswerComposer.TryComposeAnalysisCli(request, out var answer));
        Assert.Contains("qacli analyze -cf -P<directory>", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("`qacli analyze -cf -P<directory> 解析を開始します。`", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentEvidenceFragmentsAreNeverJoined()
    {
        var request = Request([
            Source("prefix", "qacli analyze"),
            Source("option", "-P<directory>")
        ]);

        Assert.False(HowToAnswerComposer.TryComposeAnalysisCli(request, out _));
    }

    [Fact]
    public void ProvenanceReportsRawNormalizedSpanAndIntegrity()
    {
        var source = Source("manual-40", "qacli analyze -cf -P<directory>-F<file-with-list>PerforceQAC") with
        {
            PageNumber = 40,
            SectionTitle = "解析の実行"
        };

        var record = Assert.Single(HowToAnswerComposer.ExtractAnalysisCommandProvenance(source));

        Assert.Equal(HowToAnswerComposer.CliCommandIntegrity.Ambiguous, record.Integrity);
        Assert.Equal("manual-40", record.SourceEvidenceId);
        Assert.Equal("QAC Manual", record.DocumentTitle);
        Assert.Equal(40, record.PageNumber);
        Assert.Equal("解析の実行", record.SectionTitle);
        Assert.Equal(record.RawCommandText, record.NormalizedCommandText);
        Assert.True(record.End > record.Start);
    }

    [Fact]
    public void JapaneseProseAfterASeparatedCommandIsNotPartOfProvenance()
    {
        var source = Source("manual-41", "qacli analyze -P<directory>\r\n解析を開始します。");

        var record = Assert.Single(HowToAnswerComposer.ExtractAnalysisCommandProvenance(source));

        Assert.Equal(HowToAnswerComposer.CliCommandIntegrity.Complete, record.Integrity);
        Assert.DoesNotContain("解析を開始", record.CommandText, StringComparison.Ordinal);
    }

    private static AnswerDraftRequest Request(IReadOnlyList<SearchSource> sources) => new()
    {
        Case = new CaseContext { ProductName = "HelixQAC" },
        InquiryText = "QACの解析CLIコマンドとオプションを教えてください。",
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
