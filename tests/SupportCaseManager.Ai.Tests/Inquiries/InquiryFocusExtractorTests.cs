using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Inquiries;

namespace SupportCaseManager.Ai.Tests.Inquiries;

public sealed class InquiryFocusExtractorTests
{
    [Fact]
    public void Extract_PrioritizesQuestionSection()
    {
        var focus = new InquiryFocusExtractor().Extract("""
            お世話になっております。
            [質問]
            ライセンス認証エラーで起動できません。
            何卒よろしくお願いいたします。
            """);

        Assert.Contains("ライセンス認証エラー", focus.FocusText);
        Assert.DoesNotContain("お世話", focus.FocusText);
    }

    [Fact]
    public void Extract_KeepsImportantJapaneseTerms()
    {
        var focus = new InquiryFocusExtractor().Extract("""
            ライセンス認証エラーで製品が起動できません。
            ライセンスサーバー名、ポート番号、ファイアウォール設定を確認したいです。
            """);

        Assert.Contains("ライセンス認証エラー", focus.ImportantTerms);
        Assert.Contains("ライセンスサーバー名", focus.ImportantTerms);
        Assert.Contains("ポート番号", focus.ImportantTerms);
        Assert.Contains("ファイアウォール設定", focus.ImportantTerms);
    }

    [Fact]
    public void Extract_ExcludesGreetingAndSignatureNoise()
    {
        var focus = new InquiryFocusExtractor().Extract("よろしくお願いいたします。ライセンス認証エラーです。サポートチーム");

        Assert.Contains("よろしく", focus.ExcludedTerms);
        Assert.DoesNotContain("よろしく", focus.ImportantTerms);
    }

    [Fact]
    public void Extract_DetectsFreshnessSensitiveQuery()
    {
        var focus = new InquiryFocusExtractor().Extract("最新バージョンとEP/HF、サポート期限を教えてください。");

        Assert.True(focus.IsFreshnessSensitive);
        Assert.Contains("最新", focus.FreshnessReason);
    }

    [Fact]
    public void Extract_DetectsTargetVersions()
    {
        var focus = new InquiryFocusExtractor().Extract("CxSAST 9.6 と 9.7 のRelease NotesとHotfixを確認したいです。");

        Assert.Contains("9.6", focus.TargetVersions);
        Assert.Contains("9.7", focus.TargetVersions);
        Assert.Contains("9.6", focus.ImportantTerms);
    }

    [Fact]
    public void Extract_RemovesCurrentCustomerTermsFromImportantTerms()
    {
        var focus = new InquiryFocusExtractor().Extract(
            "株式会社サンプルのライセンス認証エラーです。",
            new CaseContext { CompanyName = "株式会社サンプル" });

        Assert.DoesNotContain("株式会社サンプル", focus.ImportantTerms);
        Assert.Contains("ライセンス認証エラー", focus.ImportantTerms);
    }

    [Fact]
    public void Extract_DoesNotTreatSupportSignatureAsPortQuestion()
    {
        var focus = new InquiryFocusExtractor().Extract("""
            東陽テクニカ テクニカルサポートご担当者様
            Dashboard利用手順書を提供していただけないかお願いしたく、ご連絡いたしました。
            具体的な利用方法や設定手順、トラブルシューティングの情報などが含まれている手順書をご提供いただけますと幸いです。
            Yuto Yoshihara
            """);

        Assert.DoesNotContain("ポート", focus.ImportantTerms);
        Assert.DoesNotContain("port", focus.ImportantTerms, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(focus.ImportantTerms, term => term.Contains("Dashboard", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(focus.ImportantTerms, term => term.Contains("手順書", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_SimpleInstallQuestionKeepsReadableTermsWithoutFragmentNoise()
    {
        var focus = new InquiryFocusExtractor().Extract("QACのインストール方法を教えてください。");

        Assert.False(focus.IsFreshnessSensitive);
        Assert.Contains("QAC", focus.ImportantTerms, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("インストール方法", focus.ImportantTerms);
        Assert.DoesNotContain(focus.ImportantTerms, term =>
            term is "ACのイン" or "Cのインス" or "教えてくだ" or "えてくださ");
    }

    [Fact]
    public void Extract_ValidatePermissionInquiryDoesNotTreatEnvironmentOrStepNumbersAsFreshness()
    {
        var focus = new InquiryFocusExtractor().Extract("""
            件名:【質問】Validate アップロード時に権限不十分エラー
            現在、Validate利用手順書に従って作業を進めております。
            ■環境:
            QACバージョン: QAC 2025.4 (QAC 12.3.0)
            Validate: p4-validate-installer.25.4.0.61.win64.exe
            ■手順:
            [2.1 インストール]、[2.2 接続確認]を行った後、解析結果をアップロードすると権限が不十分です。
            TEL: 070-6963-1508
            E-mail: user@example.com
            """);

        Assert.False(focus.IsFreshnessSensitive);
        Assert.Contains("Validate", focus.ImportantTerms, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("アップロード", focus.ImportantTerms);
        Assert.Contains("権限不足", focus.ImportantTerms);
        Assert.DoesNotContain("2.1", focus.TargetVersions);
        Assert.DoesNotContain("2.2", focus.TargetVersions);
        Assert.DoesNotContain(focus.ImportantTerms, term =>
            term.Contains('@') || term.Contains("070-6963-1508", StringComparison.Ordinal));
    }
}
