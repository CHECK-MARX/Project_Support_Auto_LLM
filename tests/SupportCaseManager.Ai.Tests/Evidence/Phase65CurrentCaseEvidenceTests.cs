using System.IO.Compression;
using System.Text;
using SupportCaseManager.Ai.Core.Evidence;

namespace SupportCaseManager.Ai.Tests.Evidence;

public sealed class Phase65CurrentCaseEvidenceTests
{
    [Fact]
    public async Task BuildsAnonymousCurrentCaseEvidenceWithLogicalLocators()
    {
        using var fixture = new CaseFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "scan.csv"), "Query,Result Path\nSQL Injection,src\\app.cs:42\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "config.xml"), "<scan><Preset>Default</Preset><Framework>Classic ASP</Framework></scan>");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "inquiry.txt"), "CheckmarxのSQL Injection結果を確認してください。\nよろしくお願いします。");

        var result = await new CurrentCaseEvidenceService().BuildAsync(fixture.Root, "session-a", string.Empty);

        Assert.Contains(result.Manifest, item => item.FileName == "scan.csv" && item.ParseStatus == "PARSED");
        Assert.Contains(result.Manifest, item => item.FileName == "config.xml" && item.ParseStatus == "PARSED");
        Assert.NotEmpty(result.Evidence);
        Assert.Contains(result.Evidence, item => item.Title == "scan.csv" && item.Locator == "csv:row:2");
        Assert.Contains(result.Evidence, item => item.Title == "config.xml" && item.Locator!.StartsWith("xml:", StringComparison.Ordinal));
        Assert.All(result.Evidence, item =>
        {
            Assert.Equal("CurrentCase", item.SourceType);
            Assert.Equal("session-a", item.CaseSessionId);
            Assert.Null(item.FilePath);
            Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), item.Locator ?? string.Empty);
            Assert.False(string.IsNullOrWhiteSpace(item.ContentHash));
        });
    }

    [Fact]
    public async Task RejectsXmlExternalEntityWithoutBreakingOtherFiles()
    {
        using var fixture = new CaseFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "safe.txt"), "SQL Injection evidence");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "unsafe.xml"), "<!DOCTYPE scan [<!ENTITY xxe SYSTEM 'file:///secret.txt'>]><scan>&xxe;</scan>");

        var result = await new CurrentCaseEvidenceService().BuildAsync(fixture.Root, "session-a", "evidence");

        Assert.Contains(result.Manifest, item => item.FileName == "unsafe.xml" && item.ParseStatus is "UNREADABLE" or "UNSAFE_REJECTED");
        Assert.Contains(result.Evidence, item => item.Title == "safe.txt");
    }

    [Fact]
    public async Task RejectsZipTraversalAndNeverCreatesExtractedFiles()
    {
        using var fixture = new CaseFixture();
        var zipPath = Path.Combine(fixture.Root, "attachments.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../outside.txt");
            await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync("must not be read");
        }

        var result = await new CurrentCaseEvidenceService().BuildAsync(fixture.Root, "session-a", "outside");

        Assert.Contains(result.Manifest, item => item.FileName == "attachments.zip" && item.ParseStatus == "UNSAFE_REJECTED");
        Assert.DoesNotContain(result.Evidence, item => item.Text.Contains("must not be read", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "outside.txt")));
    }

    [Fact]
    public async Task CaseSessionIdKeepsPreviousCaseEvidenceOutsideNewSession()
    {
        using var fixture = new CaseFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "case-a.txt"), "case-a-secret-fact");
        var service = new CurrentCaseEvidenceService();

        var first = await service.BuildAsync(fixture.Root, "session-a", "case-a-secret-fact");
        var second = await service.BuildAsync(fixture.Root, "session-b", "case-b-question");

        Assert.NotEmpty(first.Evidence);
        Assert.All(second.Evidence, item => Assert.Equal("session-b", item.CaseSessionId));
        Assert.DoesNotContain(second.Evidence, item => item.CaseSessionId == "session-a");
    }

    [Fact]
    public async Task ReadsSafeZipTextEntryWithoutExtractingIt()
    {
        using var fixture = new CaseFixture();
        var zipPath = Path.Combine(fixture.Root, "source.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("src/app.cs");
            await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync("var result = 42;");
        }

        var result = await new CurrentCaseEvidenceService().BuildAsync(fixture.Root, "session-a", "result");

        Assert.Contains(result.Manifest, item => item.FileName == "source.zip" && item.ParseStatus == "PARSED");
        Assert.Contains(result.Evidence, item => item.EvidenceKind == "ZipSource" && item.Locator == "zip:src/app.cs:line:1");
        Assert.False(File.Exists(Path.Combine(fixture.Root, "src", "app.cs")));
    }

    private sealed class CaseFixture : IDisposable
    {
        public CaseFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "SupportCaseManager", "phase65", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
