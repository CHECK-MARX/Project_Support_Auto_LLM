using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using Xunit;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class Phase47ProcedureDeliveryTests
{
    [Fact]
    public void HowToSectionsFollowEvidenceMeaningInsteadOfEvidencePosition()
    {
        var answer = DeterministicAnswerComposer.ComposeHowTo([
            Evidence("command", "qacli analyze -P <project-directory> を実行します。"),
            Evidence("verification", "解析完了後、解析結果と問題パネルを確認します。"),
        ]);

        var cli = Section(answer, "【CLIでの手順】", "【結果確認】");
        var verification = Section(answer, "【結果確認】", "【注意点】");
        Assert.Contains("qacli analyze -P <project-directory>", cli, StringComparison.Ordinal);
        Assert.Contains("解析完了後", verification, StringComparison.Ordinal);
        Assert.Contains("確認できません", Section(answer, "【事前準備】", "【プロジェクト作成】"), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingVerificationEvidenceIsNotReplacedWithGenericCompletionClaim()
    {
        var answer = DeterministicAnswerComposer.ComposeHowTo([
            Evidence("procedure", "プロジェクトを作成し、qacli analyze -P <project-directory> を実行します。"),
        ]);

        var verification = Section(answer, "【結果確認】", "【注意点】");
        Assert.Contains("確認できません", verification, StringComparison.Ordinal);
        Assert.DoesNotContain("エラーがなければ完了", verification, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalysisHowToDoesNotRenderValidateCommandAsCoreProcedure()
    {
        var request = new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = "HelixQAC" },
            InquiryText = "QACでプロジェクトを解析する手順を教えてください。",
            Sources = [
                new SearchSource
                {
                    SourceId = "analysis",
                    SourceType = "Manual",
                    Title = "Project analysis",
                    Text = "qacli analyze -P <project-directory> を実行します。解析結果を確認します.",
                },
                new SearchSource
                {
                    SourceId = "validate",
                    SourceType = "OfficialDoc",
                    Title = "Validate upload",
                    Text = "qacli validate build --qaf-project <project> で解析結果をアップロードします。",
                },
            ],
        };

        Assert.True(HowToAnswerComposer.IsAnalysisHowTo(request));
        Assert.True(HowToAnswerComposer.TryComposeAnalysis(request, out var answer));
        Assert.Contains("qacli analyze", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("qacli validate", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalysisTroubleshootingDoesNotFallBackToValidateEvidence()
    {
        var request = new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = "HelixQAC" },
            InquiryText = "QAC解析が失敗した場合の確認手順を教えてください。",
            Sources = [new SearchSource
            {
                SourceId = "validate",
                SourceType = "OfficialDoc",
                Title = "Validate upload",
                Text = "qacli validate build で解析結果をアップロードします。",
            }],
        };

        Assert.True(HowToAnswerComposer.IsAnalysisHowTo(request));
        Assert.True(HowToAnswerComposer.TryComposeAnalysis(request, out var answer));
        Assert.DoesNotContain("qacli validate", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("確認できません", answer, StringComparison.Ordinal);
    }

    private static EvidenceItem Evidence(string id, string excerpt) => new()
    {
        SourceId = id,
        SourceType = "Manual",
        DocumentTitle = "QAC Manual",
        Excerpt = excerpt,
    };

    private static string Section(string answer, string start, string end)
    {
        var startIndex = answer.IndexOf(start, StringComparison.Ordinal);
        var endIndex = answer.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        return answer[startIndex..(endIndex < 0 ? answer.Length : endIndex)];
    }
}
