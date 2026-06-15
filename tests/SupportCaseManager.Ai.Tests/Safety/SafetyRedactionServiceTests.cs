using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Tests.Safety;

public class SafetyRedactionServiceTests
{
    [Fact]
    public void RedactForLog_MasksEmailAddress()
    {
        var service = new SafetyRedactionService();

        var result = service.RedactForLog("連絡先は user@example.com です。");

        Assert.DoesNotContain("user@example.com", result);
        Assert.Contains("[メールアドレス]", result);
    }

    [Fact]
    public void RedactForLog_MasksPhoneNumber()
    {
        var service = new SafetyRedactionService();

        var result = service.RedactForLog("電話番号は 03-1234-5678 です。");

        Assert.DoesNotContain("03-1234-5678", result);
        Assert.Contains("[電話番号]", result);
    }

    [Fact]
    public void RedactForLog_MasksWindowsAbsolutePath()
    {
        var service = new SafetyRedactionService();

        var result = service.RedactForLog(@"参照: C:\Support\Cases\SUP-001\note.txt");

        Assert.DoesNotContain(@"C:\Support\Cases", result);
        Assert.Contains("[Windowsパス]", result);
    }

    [Fact]
    public void RedactForLog_MasksApiKeyLikeText()
    {
        var service = new SafetyRedactionService();

        var result = service.RedactForLog("api_key=sk-1234567890abcdef");

        Assert.DoesNotContain("sk-1234567890abcdef", result);
        Assert.Contains("[APIキー]", result);
    }

    [Fact]
    public void RemoveInternalReferencesFromCustomerReply_RemovesEvidenceIds()
    {
        var service = new SafetyRedactionService();

        var result = service.RemoveInternalReferencesFromCustomerReply("根拠は sourceId: case:SUP-001 です。manual:install:001 も参照。");

        Assert.DoesNotContain("case:SUP-001", result);
        Assert.DoesNotContain("manual:install:001", result);
        Assert.Contains("[根拠ID削除]", result);
    }

    [Fact]
    public void FindCustomerReplyWarnings_DetectsInternalMemoLine()
    {
        var service = new SafetyRedactionService();

        var warnings = service.FindCustomerReplyWarnings("本文です。\n社内メモ: 類似案件を参照しました。");

        Assert.Contains(warnings, warning => warning.Contains("社内メモ", StringComparison.Ordinal));
    }
}
