using System.Security.Cryptography;
using System.Text;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Tests.Facts;

public sealed class CurrentCaseFactProjectorTests
{
    private static SearchSource Record(string text, string kind = "AuditedAttachment") => new()
    {
        SourceId = "case:verified-file", SourceType = "CurrentCase", ParseStatus = "AUDITED",
        Text = text, ContentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
        EvidenceKind = kind, Title = "監査済み添付",
    };

    [Fact]
    public void AuditedFactRetainsCaseAttributionAndOriginalLocator()
    {
        var record = Record("添付コードにmax-age=31536000およびincludeSubDomainsの設定がある。");
        var fact = Assert.Single(CurrentCaseFactProjector.Project("ヘッダー設定を確認", [record], new SafetyRedactionService()));
        Assert.Equal("CurrentCase", fact.SourceType);
        Assert.Equal("Candidate", fact.Status);
        Assert.Equal(record.SourceId, fact.EvidenceId);
        Assert.Contains("span:0:", fact.Section);
        Assert.Contains("製品仕様", fact.Statement);
        Assert.Contains("31536000", fact.Value);
    }

    [Fact]
    public void RejectsUnverifiedChangedAndOfficialRecords()
    {
        var record = Record("添付コードにmax-age=31536000の設定がある。");
        Assert.Empty(CurrentCaseFactProjector.Project("コード設定", [record with { ParseStatus = "image_view_pending" },
            record with { Text = "変更された記録" }, record with { SourceType = "OfficialDoc" }], new SafetyRedactionService()));
    }

    [Fact]
    public void DoesNotTurnHypothesesOrInventoryIntoObservedContent()
    {
        var record = Record("添付から仕様が変更された可能性がある。\n.png:2\n内容は未確認。");
        Assert.Empty(CurrentCaseFactProjector.Project("製品仕様", [record], new SafetyRedactionService()));
    }
}
