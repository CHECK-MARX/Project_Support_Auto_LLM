using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class CustomerReplyRecipientFormatterTests
{
    [Theory]
    [InlineData("TOYO\nご担当者様\n\n回答本文")]
    [InlineData("東陽テクニカ\r\nご担当者様\r\n\r\n回答本文")]
    [InlineData("回答本文")]
    public void EnsureHeader_MissingRecipientAlwaysUsesPlaceholders(string reply)
    {
        var result = CustomerReplyRecipientFormatter.EnsureHeader(
            new CaseContext(),
            reply);

        Assert.StartsWith($"[会社名]{Environment.NewLine}[お客様名] 様", result);
        Assert.DoesNotContain("TOYO", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("東陽テクニカ", result, StringComparison.Ordinal);
        Assert.DoesNotContain("ご担当者様", result, StringComparison.Ordinal);
        Assert.Contains("回答本文", result, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureHeader_KnownRecipientKeepsActualValues()
    {
        var result = CustomerReplyRecipientFormatter.EnsureHeader(
            new CaseContext { CompanyName = "顧客株式会社", CustomerName = "山田 太郎" },
            "回答本文");

        Assert.StartsWith($"顧客株式会社{Environment.NewLine}山田 太郎 様", result);
    }

    [Fact]
    public void EnsureHeader_RemovesDuplicatePlaceholderRecipientFromBody()
    {
        var result = CustomerReplyRecipientFormatter.EnsureHeader(
            new CaseContext(),
            "[会社名]\n[お客様名] 様\n\n[お客様名] 様\n\n回答本文");

        Assert.Equal(1, CountOccurrences(result, "[会社名]"));
        Assert.Equal(1, CountOccurrences(result, "[お客様名] 様"));
        Assert.Contains("回答本文", result, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string term)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(term, offset, StringComparison.Ordinal)) >= 0)
        {
            count += 1;
            offset += term.Length;
        }
        return count;
    }
}
