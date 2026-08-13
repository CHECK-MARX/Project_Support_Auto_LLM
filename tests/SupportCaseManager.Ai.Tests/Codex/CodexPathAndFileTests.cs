using System.IO.Compression;
using System.Text;
using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class CodexPathAndFileTests
{
    [Fact]
    public void TryNormalizeFileWithinRoot_AcceptsInsideAndRejectsTraversal()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        var insideFile = Path.Combine(root.Path, "inside.log");
        var outsideFile = Path.Combine(outside.Path, "outside.log");
        File.WriteAllText(insideFile, "inside");
        File.WriteAllText(outsideFile, "outside");

        Assert.True(CodexPathPolicy.TryNormalizeFileWithinRoot(root.Path, insideFile, out _, out _));
        Assert.False(CodexPathPolicy.TryNormalizeFileWithinRoot(root.Path, outsideFile, out _, out var error));
        Assert.Contains("案件フォルダ外", error);
    }

    [Fact]
    public async Task Scanner_ClassifiesImagesLogsAndExcludesArchivesAndUnsupportedFiles()
    {
        using var root = new TempDirectory();
        File.WriteAllText(Path.Combine(root.Path, "trace.log"), "error");
        File.WriteAllBytes(Path.Combine(root.Path, "screen.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root.Path, "bundle.zip"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root.Path, "binary.exe"), [1, 2, 3]);

        var result = await new CodexCaseFileScanner().ScanAsync(root.Path);

        Assert.True(result.Files.Single(file => file.FileName == "trace.log").CanSendToCodex);
        Assert.True(result.Files.Single(file => file.FileName == "screen.png").IsImageInput);
        Assert.True(result.Files.Single(file => file.FileName == "bundle.zip").CanSendToCodex);
        Assert.False(result.Files.Single(file => file.FileName == "binary.exe").CanSendToCodex);
    }

    [Fact]
    public async Task AttachmentReader_NormalizesCommonEncodingsAndExtractsImportantLargeLogLines()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(
            Path.Combine(root.Path, "shift-jis.log"),
            Encoding.GetEncoding(932).GetBytes("開始\r\nアップロード権限エラー\r\n終了"));
        await File.WriteAllTextAsync(
            Path.Combine(root.Path, "utf16.log"),
            "開始\r\nValidate接続失敗\r\n終了",
            Encoding.Unicode);
        var largeLines = Enumerable.Range(1, 8_000)
            .Select(index => index == 4_000 ? "18:10:42 ERROR_UPLOAD_42 permission denied" : $"18:10:{index % 60:00} TRACE normal operation {index}");
        await File.WriteAllLinesAsync(Path.Combine(root.Path, "large.log"), largeLines, Encoding.UTF8);
        var zipPath = Path.Combine(root.Path, "logs.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("inner.log");
            await using var stream = entry.Open();
            var payload = Encoding.GetEncoding(932).GetBytes("ZIP内ログ: upload failed");
            await stream.WriteAsync(payload);
        }
        var scan = await new CodexCaseFileScanner().ScanAsync(root.Path);

        var result = await new CodexAttachmentContentReader().ReadAsync(
            root.Path,
            scan.Files.Where(static file => file.CanSendToCodex).ToArray());

        Assert.Contains(result.Contents, item => item.RelativePath == "shift-jis.log"
            && item.EncodingName.Contains("Shift-JIS")
            && item.Content.Contains("アップロード権限エラー"));
        Assert.Contains(result.Contents, item => item.RelativePath == "utf16.log"
            && item.EncodingName.Contains("UTF-16")
            && item.Content.Contains("Validate接続失敗"));
        Assert.Contains(result.Contents, item => item.RelativePath == "large.log"
            && item.IsTruncated
            && item.Content.Contains("ERROR_UPLOAD_42"));
        Assert.Contains(result.Contents, item => item.RelativePath == "logs.zip"
            && item.Content.Contains("ZIP内ログ: upload failed"));
    }

    [Fact]
    public async Task Scanner_DoesNotFollowDirectoryLinks()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        File.WriteAllText(Path.Combine(outside.Path, "secret.txt"), "secret");
        var link = Path.Combine(root.Path, "outside-link");
        try
        {
            Directory.CreateSymbolicLink(link, outside.Path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var result = await new CodexCaseFileScanner().ScanAsync(root.Path);

        Assert.DoesNotContain(result.Files, file => file.FileName == "secret.txt");
        Assert.Contains(result.Warnings, warning => warning.Contains("リンク先フォルダ"));
    }
}
