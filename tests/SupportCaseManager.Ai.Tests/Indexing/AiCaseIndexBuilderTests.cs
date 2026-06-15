using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Core.Cases;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Notes;
using SupportCaseManager.Ai.Tests.Helpers;
using SupportCaseManager.Core.Notes;

namespace SupportCaseManager.Ai.Tests.Indexing;

public class AiCaseIndexBuilderTests
{
    [Fact]
    public async Task BuildAsync_WritesIndexUnderAiIndexFolder()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "closed");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var caseFolder = CreateCaseFolder(sourceFolder);
        await File.WriteAllTextAsync(
            Path.Combine(caseFolder, "お客様ご相談内容_00001234.txt"),
            "起動時にエラーが発生します。回避策として設定ファイルを確認しました。",
            Encoding.UTF8);
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(sourceFolder, aiIndexFolder);

        Assert.Equal(1, result.IndexedCaseCount);
        Assert.Equal(1, result.IndexedNoteCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(Path.Combine(aiIndexFolder, AiCaseIndexBuilder.IndexFileName), result.IndexFilePath);
        Assert.True(File.Exists(result.IndexFilePath));

        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.Single(document.Notes);
        Assert.Equal("00001234", document.Notes[0].SupportNumber);
        Assert.Equal("株式会社サンプル", document.Notes[0].CompanyName);
        Assert.Contains("起動時にエラー", document.Notes[0].Text);
    }

    [Fact]
    public async Task BuildAsync_CreatesAiIndexFolderWhenMissing()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "closed");
        var aiIndexFolder = Path.Combine(temp.Path, "missing-ai-index");
        var caseFolder = CreateCaseFolder(sourceFolder);
        await File.WriteAllTextAsync(Path.Combine(caseFolder, "note.txt"), "検索用メモ", Encoding.UTF8);
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(sourceFolder, aiIndexFolder);

        Assert.True(Directory.Exists(aiIndexFolder));
        Assert.True(File.Exists(result.IndexFilePath));
    }

    [Fact]
    public async Task BuildAsync_DoesNotModifyExistingNoteFile()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "closed");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var caseFolder = CreateCaseFolder(sourceFolder);
        var notePath = Path.Combine(caseFolder, "お客様への返信案_00001234.txt");
        await File.WriteAllTextAsync(notePath, "既存ノート本文", Encoding.UTF8);
        var expectedLastWriteTime = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(notePath, expectedLastWriteTime);
        var builder = CreateBuilder();

        _ = await builder.BuildAsync(sourceFolder, aiIndexFolder);

        Assert.Equal("既存ノート本文", await File.ReadAllTextAsync(notePath));
        Assert.Equal(expectedLastWriteTime, File.GetLastWriteTime(notePath));
    }

    [Fact]
    public async Task BuildAsync_DoesNotCreateExistingAppDataFiles()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "closed");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var caseFolder = CreateCaseFolder(sourceFolder);
        await File.WriteAllTextAsync(Path.Combine(caseFolder, "note.txt"), "検索用メモ", Encoding.UTF8);
        var builder = CreateBuilder();

        _ = await builder.BuildAsync(sourceFolder, aiIndexFolder);

        Assert.False(File.Exists(Path.Combine(sourceFolder, "cases-index.json")));
        Assert.False(File.Exists(Path.Combine(sourceFolder, "user-settings.json")));
        Assert.False(File.Exists(Path.Combine(caseFolder, "cases-index.json")));
        Assert.False(File.Exists(Path.Combine(caseFolder, "user-settings.json")));
    }

    [Fact]
    public async Task BuildAsync_PreservesJapaneseTextAndWindowsPathText()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "closed");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var caseFolder = CreateCaseFolder(sourceFolder);
        var text = "日本語メモです。\r\nWindowsパス: D:\\Support\\Cases\\00001234";
        await File.WriteAllTextAsync(Path.Combine(caseFolder, "メーカー連携内容_00001234.txt"), text, Encoding.UTF8);
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(sourceFolder, aiIndexFolder);

        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.Single(document.Notes);
        Assert.Contains("日本語メモ", document.Notes[0].Text);
        Assert.Contains(@"D:\Support\Cases\00001234", document.Notes[0].Text);
    }

    [Fact]
    public async Task BuildAsync_MissingSourceFolder_WritesEmptyIndexAndWarning()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "missing");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(sourceFolder, aiIndexFolder);

        Assert.Equal(0, result.IndexedCaseCount);
        Assert.Equal(0, result.IndexedNoteCount);
        Assert.Equal(1, result.ErrorCount);
        Assert.NotEmpty(result.Warnings);
        Assert.True(File.Exists(result.IndexFilePath));
    }

    [Fact]
    public async Task BuildAsync_ReturnsCaseIndexDiagnostics()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "closed");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var caseFolder = CreateCaseFolder(sourceFolder);
        await File.WriteAllTextAsync(
            Path.Combine(caseFolder, NoteDefinitions.All[0].FileName("00001234")),
            "known note text",
            Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(caseFolder, "unknown.txt"), "unknown note text", Encoding.UTF8);

        var result = await CreateBuilder().BuildAsync(sourceFolder, aiIndexFolder);

        Assert.Equal(1, result.ScannedCaseFolderCount);
        Assert.Equal(2, result.ScannedNoteFileCount);
        Assert.Equal(2, result.SupportNumberExtractedCount);
        Assert.Equal(0, result.MissingSupportNumberCount);
        Assert.Equal(1, result.NoteKindExtractedCount);
        Assert.Equal(1, result.UnknownNoteKindCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("Note kind", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_SkipsEmptyNoteAndCountsDiagnostic()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "closed");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var caseFolder = CreateCaseFolder(sourceFolder);
        await File.WriteAllTextAsync(Path.Combine(caseFolder, "empty.txt"), " \r\n\t ", Encoding.UTF8);

        var result = await CreateBuilder().BuildAsync(sourceFolder, aiIndexFolder);

        Assert.Equal(1, result.ScannedNoteFileCount);
        Assert.Equal(1, result.EmptyNoteSkippedCount);
        Assert.Equal(0, result.IndexedNoteCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_CountsMissingSupportNumber()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "closed");
        var caseFolder = Path.Combine(sourceFolder, "20260602(CompanyOnly)対応中");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(caseFolder);
        await File.WriteAllTextAsync(Path.Combine(caseFolder, "note.txt"), "note text", Encoding.UTF8);

        var result = await CreateBuilder().BuildAsync(sourceFolder, aiIndexFolder);

        Assert.Equal(1, result.ScannedCaseFolderCount);
        Assert.Equal(0, result.SupportNumberExtractedCount);
        Assert.Equal(1, result.MissingSupportNumberCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("Support number", StringComparison.OrdinalIgnoreCase));
    }

    private static AiCaseIndexBuilder CreateBuilder()
    {
        return new AiCaseIndexBuilder(
            new CaseContextBuilder(new NoteSnapshotReader()),
            () => new DateTimeOffset(2026, 6, 2, 17, 15, 0, TimeSpan.FromHours(9)));
    }

    private static string CreateCaseFolder(string sourceFolder)
    {
        var caseFolder = Path.Combine(sourceFolder, "20260602(株式会社サンプル_00001234)対応中_20260602");
        Directory.CreateDirectory(caseFolder);
        return caseFolder;
    }

    private static async Task<AiIndexDocument> ReadIndexAsync(string indexFilePath)
    {
        await using var stream = File.OpenRead(indexFilePath);
        return await JsonSerializer.DeserializeAsync<AiIndexDocument>(stream)
            ?? throw new InvalidOperationException("Index JSON could not be deserialized.");
    }
}
