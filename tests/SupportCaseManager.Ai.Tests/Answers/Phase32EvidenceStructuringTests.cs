using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using Xunit;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class Phase32EvidenceStructuringTests
{
    [Fact]
    public void OptionDescriptionsAreMappedToTheMatchingOptionOnly()
    {
        var source = Source(
            "manual-40",
            "qacli analyze -cf -P <directory> -F <file-with-list>。\n" +
            "-P <directory> は解析対象プロジェクトを指定します。\n" +
            "-F <file-with-list> は対象ファイル一覧を指定します。\n" +
            "【次の見出し】\n" +
            "別の手順本文です。",
            pageNumber: 40,
            sectionTitle: "解析の実行");

        var options = HowToAnswerComposer.ExtractAnalysisOptionProvenance(source);

        Assert.Equal(3, options.Count);
        Assert.Equal("-cf", options[0].OptionText);
        Assert.Equal(string.Empty, options[0].Description);
        Assert.Equal("-P <directory>", options[1].OptionText);
        Assert.Equal("は解析対象プロジェクトを指定します。", options[1].Description);
        Assert.Equal("-F <file-with-list>", options[2].OptionText);
        Assert.Equal("は対象ファイル一覧を指定します。", options[2].Description);
        Assert.All(options, option =>
        {
            Assert.Equal("manual-40", option.SourceEvidenceId);
            Assert.Equal(40, option.PageNumber);
            Assert.Equal("解析の実行", option.SectionTitle);
        });
    }

    [Fact]
    public void OptionDescriptionDoesNotCrossHeadingOrParagraphBoundary()
    {
        var request = Request(
            "qacli analyze -P <directory>。\n" +
            "【確認】\n" +
            "解析結果を確認します。");

        Assert.True(HowToAnswerComposer.TryComposeAnalysisCli(request, out var answer));
        Assert.Contains("-P <directory>", answer, StringComparison.Ordinal);
        Assert.Contains("この根拠ではオプションの詳細説明までは確認できません", answer, StringComparison.Ordinal);
        var optionSection = answer[(answer.IndexOf("【オプション】", StringComparison.Ordinal))..answer.IndexOf("【実行後の確認】", StringComparison.Ordinal)];
        Assert.DoesNotContain("【確認】", optionSection, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPrerequisiteAndVerificationAreReportedWithoutInference()
    {
        var request = Request("qacli analyze -P <directory>。");

        Assert.True(HowToAnswerComposer.TryComposeAnalysisCli(request, out var answer));
        Assert.Contains("【前提条件】", answer, StringComparison.Ordinal);
        Assert.Contains("選択された根拠から確認できません", answer, StringComparison.Ordinal);
        Assert.Contains("【実行後の確認】", answer, StringComparison.Ordinal);
        Assert.Contains("選択された根拠から確認できません", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationEvidenceIsStructuredWhenPresent()
    {
        var request = Request(
            "qacli analyze -P <directory>。\n" +
            "Progress(...): analysis done。 Successes and failures を確認します。");

        Assert.True(HowToAnswerComposer.TryComposeAnalysisCli(request, out var answer));
        Assert.Contains("【実行後の確認】", answer, StringComparison.Ordinal);
        Assert.Contains("Progress(...): ... done", answer, StringComparison.Ordinal);
        Assert.Contains("Successes and failures", answer, StringComparison.Ordinal);
    }

    private static AnswerDraftRequest Request(string text) => new()
    {
        Case = new CaseContext { ProductName = "HelixQAC" },
        InquiryText = "QACの解析CLIコマンドとオプションを教えてください。",
        Sources = [Source("manual", text)],
        Settings = new AiAssistantSettings { MaxEvidenceItems = 5 },
    };

    private static SearchSource Source(
        string id,
        string text,
        int? pageNumber = null,
        string? sectionTitle = null) => new()
    {
        SourceId = id,
        SourceType = "Manual",
        Title = "QAC Manual",
        DocumentTitle = "QAC Manual",
        Text = text,
        Score = 0.9,
        PageNumber = pageNumber,
        SectionTitle = sectionTitle,
    };
}
