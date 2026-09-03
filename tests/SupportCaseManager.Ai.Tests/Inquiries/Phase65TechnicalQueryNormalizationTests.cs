using SupportCaseManager.Ai.Core.Inquiries;

namespace SupportCaseManager.Ai.Tests.Inquiries;

public sealed class Phase65TechnicalQueryNormalizationTests
{
    [Fact]
    public void A_AnonymousSignaturePersonIsExcluded() => Assert.DoesNotContain("架空担当者", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.Ordinal);
    [Fact]
    public void B_AnonymousDepartmentIsExcluded() => Assert.DoesNotContain("架空技術部", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.Ordinal);
    [Fact]
    public void C_PhoneNumberIsExcluded() => Assert.DoesNotContain("03-1234-5678", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.Ordinal);
    [Fact]
    public void D_EmailAddressIsExcluded() => Assert.DoesNotContain("contact@example.invalid", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.OrdinalIgnoreCase);
    [Fact]
    public void E_GreetingIsExcluded() => Assert.DoesNotContain("お世話になっております", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.Ordinal);
    [Fact]
    public void F_ClosingBoilerplateIsExcluded() => Assert.DoesNotContain("何卒よろしくお願いします", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.Ordinal);
    [Fact]
    public void G_SqlInjectionIsPreserved() => Assert.Contains("SQL Injection", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.OrdinalIgnoreCase);
    [Fact]
    public void H_SanitizerIsPreserved() => Assert.Contains("Sanitizer", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.OrdinalIgnoreCase);
    [Fact]
    public void I_FalsePositiveIsPreserved() => Assert.Contains("False Positive", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.OrdinalIgnoreCase);
    [Fact]
    public void J_ClassicAspIsPreserved() => Assert.Contains("Classic ASP", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.OrdinalIgnoreCase);
    [Fact]
    public void K_QueryIsPreserved() => Assert.Contains("Query", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.OrdinalIgnoreCase);
    [Fact]
    public void L_FrameworkIsPreserved() => Assert.Contains("Framework", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.OrdinalIgnoreCase);
    [Fact]
    public void M_TechnicalAttachmentFileNameRemainsAvailable() => Assert.Contains("scan-result.pdf", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.OrdinalIgnoreCase);
    [Fact]
    public void M2_AttachmentFileNameDoesNotBecomeASectionMarker()
    {
        var focus = ExtractFixture();
        Assert.Contains("SQL Injection", focus.TechnicalQuery.CoreQuestion, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("検出が継続", focus.TechnicalQuery.CoreQuestion, StringComparison.Ordinal);
    }
    [Fact]
    public void N_NormalTechnicalSentenceIsNotOverRemoved() => Assert.Contains("検出が継続", ExtractFixture().TechnicalQuery.CoreQuestion, StringComparison.Ordinal);
    [Fact]
    public void O_ShortTechnicalQueryRemainsNonEmpty() => Assert.NotEmpty(new InquiryFocusExtractor().Extract("エラーです。").TechnicalQuery.CoreQuestion);

    private static SupportCaseManager.Ai.Contracts.InquiryFocus ExtractFixture() => new InquiryFocusExtractor().Extract("""
        架空会社株式会社の架空担当者です。
        お世話になっております。
        お問い合わせ内容:
        SQL Injectionの検出が過検知か確認したいです。
        Sanitizer処理後もFalse Positiveの検出が継続します。
        Classic ASPのQueryとFramework設定を確認してください。
        添付: scan-result.pdf、source-code.zip
        お忙しいところ恐縮ですが、何卒よろしくお願いします。
        ---
        架空会社株式会社
        架空技術部
        架空担当者
        電話: 03-1234-5678
        contact@example.invalid
        """);
}
