using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Notes;

namespace SupportCaseManager.Tests.Cases;

public sealed class GptCaseHandoffBriefBuilderTests
{
    [Fact]
    public void Build_UsesLatestCustomerEntryAsCurrentDelta()
    {
        var brief = new GptCaseHandoffBriefBuilder().Build(Source(
            customer: """
                *****追記部_2026/09/10 10:00:00(回答済み)******
                過去のT-SQL質問
                --------------------------------------------------
                *****追記部_2026/09/16 10:00:00(追加質問)******
                現在のDefault Config質問
                --------------------------------------------------
                """,
            reply: "T-SQLについて回答済みです。"));

        var currentSection = Between(brief, "【現在の最新問い合わせ】", "【現在の未解決事項】");
        var answeredSection = Between(brief, "【回答済み・解決済み事項】", "【メーカーとのこれまでのやり取り】");
        Assert.Contains("現在のDefault Config質問", currentSection, StringComparison.Ordinal);
        Assert.DoesNotContain("過去のT-SQL質問", currentSection, StringComparison.Ordinal);
        Assert.DoesNotContain("過去のT-SQL質問", answeredSection, StringComparison.Ordinal);
        Assert.Contains("T-SQLについて回答済み", answeredSection, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RemovesEmailPhoneAndBoilerplate()
    {
        var brief = new GptCaseHandoffBriefBuilder().Build(Source(
            customer: "担当者 user@example.com 03-1234-5678 からの質問です。\nBest regards,"));

        Assert.DoesNotContain("user@example.com", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("03-1234-5678", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Best regards", brief, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_IncludesMetadataAndFileNamesWithoutFileContent()
    {
        var brief = new GptCaseHandoffBriefBuilder().Build(Source(
            customer: "現在の質問",
            relatedFiles: ["Evidence.xlsx", "Guide.pdf"]));

        Assert.Contains("Support ID：00018303", brief, StringComparison.Ordinal);
        Assert.Contains("製品：Checkmarx", brief, StringComparison.Ordinal);
        Assert.Contains("Evidence.xlsx", brief, StringComparison.Ordinal);
        Assert.Contains("Guide.pdf", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedParser_PicksNewestTimestampInsteadOfWholeFile()
    {
        var entries = CaseNoteHistoryParser.Parse("""
            *****追記部_2026/09/16 09:00:00(調査中)******
            first
            --------------------------------------------------
            *****追記部_2026/09/16 11:00:00(調査中)******
            latest
            --------------------------------------------------
            """);

        Assert.Equal("latest", CaseNoteHistoryParser.PickLatest(entries)?.Body.Trim());
    }

    private static GptCaseHandoffSource Source(
        string customer,
        string reply = "",
        string manufacturer = "",
        IReadOnlyList<string>? relatedFiles = null) => new(
        new CaseRecord(
            "Example Company",
            "00018303",
            "メーカー確認中",
            "20260916",
            "20260916(Example Company_00018303)",
            @"C:\Cases\20260916(Example Company_00018303)",
            "2026-09-16T00:00:00Z"),
        "Checkmarx",
        customer,
        reply,
        manufacturer,
        relatedFiles ?? []);

    private static string Between(string value, string start, string end)
    {
        var startIndex = value.IndexOf(start, StringComparison.Ordinal) + start.Length;
        var endIndex = value.IndexOf(end, startIndex, StringComparison.Ordinal);
        return value[startIndex..endIndex];
    }
}
