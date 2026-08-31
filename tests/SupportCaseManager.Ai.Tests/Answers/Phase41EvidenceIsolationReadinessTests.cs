using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Facts;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class Phase41EvidenceIsolationReadinessTests
{
    [Fact]
    public void GenericHowToDoesNotExposePastCaseContext()
    {
        var request = Request("QACでプロジェクトを解析する手順を教えてください。", [
            Source("manual", "qacli analyze -P <directory> を実行します。解析結果を確認します。"),
            Source("past", "00012345 株式会社サンプル。共有いただいた Helix_Generic_C.cct と default.acf を使用しました。", "PastCaseNote"),
        ]);

        var result = AnswerPostProcessor.BuildFailureFallback(request, new TimeoutException());

        Assert.Contains("qacli analyze", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("00012345", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("Helix_Generic_C.cct", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("default.acf", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains(result.Evidence, item => item.SourceType == "PastCaseNote");
    }

    [Fact]
    public void UnresolvedRequiredHowToContentIsNotCustomerReady()
    {
        var result = AnswerPostProcessor.BuildFailureFallback(
            Request("QACのCCT設定と解析前の確認方法を教えてください。", [
                Source("manual", "QACのCCT設定に関する資料です。")
            ]),
            new TimeoutException());

        Assert.Equal(AnswerReadiness.NeedsReview, result.Readiness);
    }

    [Fact]
    public void CompleteAuthorityEvidenceCanRemainCustomerReady()
    {
        var result = AnswerPostProcessor.BuildFailureFallback(
            Request("QACのCLIコマンドを教えてください。", [
                Source("manual", "qacli analyze -P <directory> を実行し、完了後に解析結果を確認します。")
            ]),
            new TimeoutException());

        Assert.Equal(AnswerReadiness.CustomerReady, result.Readiness);
    }

    [Fact]
    public void GenericValidateHowToDoesNotExposePastCaseConfiguration()
    {
        var result = AnswerPostProcessor.BuildFailureFallback(
            Request("Validateへ解析結果をアップロードする手順を教えてください。", [
                Source("manual", "qacli validate config --push -P <directory> を実行します。"),
                Source("past", "00054321。QAC-PRIVATE-PROJECT の設定と社内プロキシを使用しました。", "PastCaseNote"),
            ]),
            new TimeoutException());

        Assert.DoesNotContain("00054321", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("QAC-PRIVATE-PROJECT", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("社内プロキシ", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains(result.Evidence, item => item.SourceType == "PastCaseNote");
    }

    [Fact]
    public void GenericCheckmarxQuestionDoesNotExposeCustomerVersion()
    {
        var result = AnswerPostProcessor.BuildFailureFallback(
            Request("Checkmarxの設定手順を教えてください。", [
                Source("manual", "Checkmarxの一般的な設定手順を確認します。"),
                Source("past", "00067890。顧客環境のCheckmarx 2025.4 固有設定を使用しました。", "PastCaseNote"),
            ]),
            new TimeoutException());

        Assert.DoesNotContain("00067890", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("2025.4", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("固有設定", result.CustomerReplyDraft, StringComparison.Ordinal);
    }

    [Fact]
    public void PastCaseRemainsSupportingEvidenceWhenPrimaryEvidenceExists()
    {
        var result = AnswerPostProcessor.BuildFailureFallback(
            Request("QACのCLIコマンドを教えてください。", [
                Source("manual", "qacli analyze -P <directory> を実行します。"),
                Source("past", "00099999の過去案件では同じ解析コマンドを確認しました。", "PastCaseNote"),
            ]),
            new TimeoutException());

        Assert.Contains(result.Evidence, item => item.SourceType == "PastCaseNote");
        Assert.DoesNotContain("00099999", result.CustomerReplyDraft, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationEvidenceIsRenderedInAnalysisSection()
    {
        var result = AnswerPostProcessor.BuildFailureFallback(
            Request("QACでプロジェクトを解析する手順を教えてください。", [
                Source("manual", "qacli analyze -P <directory> を実行します。完了後、解析結果を確認します。"),
            ]),
            new TimeoutException());

        Assert.Contains("【解析結果の確認】", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("解析結果を確認", result.CustomerReplyDraft, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingVerificationEvidenceIsNotInvented()
    {
        var result = AnswerPostProcessor.BuildFailureFallback(
            Request("QACでプロジェクトを解析する手順を教えてください。", [
                Source("manual", "qacli analyze -P <directory> を実行します。"),
            ]),
            new TimeoutException());

        Assert.Contains("【解析結果の確認】", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("確認できません", result.CustomerReplyDraft, StringComparison.Ordinal);
    }

    private static AnswerDraftRequest Request(string inquiry, IReadOnlyList<SearchSource> sources) => new()
    {
        Case = new CaseContext { ProductName = "HelixQAC" },
        InquiryText = inquiry,
        Sources = sources,
        Settings = new AiAssistantSettings { MaxEvidenceItems = 5 },
    };

    private static SearchSource Source(string id, string text, string sourceType = "Manual") => new()
    {
        SourceId = id,
        SourceType = sourceType,
        Title = sourceType == "Manual" ? "QAC Manual" : "Past case",
        Text = text,
        Score = 0.9,
    };
}
