using SupportCaseManager.Ai.Core.Artifacts;
using SupportCaseManager.Ai.Core.Codex;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class ManufacturerRecipientResolverTests
{
    [Fact]
    public async Task Resolve_SelectsLatestCurrentCaseManufacturerSender()
    {
        using var temp = new TempDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "メーカー連携内容_00018742"));
        var oldPath = await WriteAsync(
            folder.FullName,
            "old.txt",
            "Date: Mon, 01 Sep 2026 10:00:00 +0900\nFrom: Old Agent <old@manufacturer.example>\nSupport ID: 00018742\n>From: Quoted Agent <quoted@manufacturer.example>");
        var latestPath = await WriteAsync(
            folder.FullName,
            "latest.txt",
            "Date: Tue, 02 Sep 2026 10:00:00 +0900\nFrom: John Smith <john.smith@manufacturer.example>\nSupport ID: 00018742\n-----Original Message-----\n>From: Old Agent <old@manufacturer.example>");
        var customerPath = await WriteAsync(
            folder.FullName,
            "customer.txt",
            "Date: Wed, 03 Sep 2026 10:00:00 +0900\nFrom: Customer Company <customer@example.com>\nSupport ID: 00018742");

        var result = new ManufacturerRecipientResolver().Resolve(
            temp.Path,
            "00018742",
            "Customer Company",
            "Customer Contact",
            [Info(oldPath), Info(latestPath), Info(customerPath)]);

        Assert.True(result.IsResolved);
        Assert.Equal("John Smith", result.DisplayName);
        Assert.Equal("john.smith@manufacturer.example", result.EmailAddress);
        Assert.Equal("latest.txt", result.SourceFileName);
        Assert.Equal("RESOLVED", result.ResolutionStatus);
        Assert.Contains("John Smith", result.DisplayText, StringComparison.Ordinal);
        Assert.DoesNotContain("Old Agent", result.DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_ReturnsUnresolvedWhenOnlyCustomerOrQuotedSenderExists()
    {
        using var temp = new TempDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "manufacturer-correspondence"));
        var path = await WriteAsync(
            folder.FullName,
            "case.txt",
            "Support ID: 00018742\n>From: Old Agent <old@manufacturer.example>\nFrom: Customer Company <customer@example.com>");

        var result = new ManufacturerRecipientResolver().Resolve(
            temp.Path,
            "00018742",
            "Customer Company",
            "Customer Contact",
            [Info(path)]);

        Assert.False(result.IsResolved);
        Assert.Equal("UNRESOLVED", result.ResolutionStatus);
        Assert.Equal("メーカー担当者を特定できませんでした", result.DisplayText);
    }

    private static CodexCaseFileInfo Info(string path) => new()
    {
        FullPath = path,
        RelativePath = Path.GetRelativePath(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!, path),
        FileName = Path.GetFileName(path),
        Kind = CodexCaseFileKind.Document,
        Size = new FileInfo(path).Length,
        LastModifiedAt = File.GetLastWriteTimeUtc(path),
        CanSendToCodex = true,
    };

    private static async Task<string> WriteAsync(string folder, string name, string text)
    {
        var path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ManufacturerRecipientResolverTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
