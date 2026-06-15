using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Indexing;

public class AiManualIndexBuilderTests
{
    [Fact]
    public async Task BuildAsync_IndexesTxtFile()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "setup.txt"), "接続エラーの確認手順です。", Encoding.UTF8);
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(manualFolder, aiIndexFolder);

        Assert.Equal(1, result.IndexedFileCount);
        Assert.Equal(1, result.IndexedChunkCount);
        Assert.True(File.Exists(Path.Combine(aiIndexFolder, AiManualIndexBuilder.IndexFileName)));
        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.Single(document.Manuals);
        Assert.Equal("Text", document.Manuals[0].DocumentType);
        Assert.Contains("接続エラー", document.Manuals[0].Text);
    }

    [Fact]
    public async Task BuildAsync_IndexesMarkdownFile()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "faq.md"), "# FAQ\r\n## 起動エラー\r\n設定を確認します。", Encoding.UTF8);
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(manualFolder, aiIndexFolder);

        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.Equal(2, result.IndexedChunkCount);
        Assert.Contains(document.Manuals, manual => manual.SectionTitle == "FAQ");
        Assert.Contains(document.Manuals, manual => manual.SectionTitle == "起動エラー");
        Assert.All(document.Manuals, manual => Assert.Equal("Markdown", manual.DocumentType));
    }

    [Fact]
    public async Task BuildAsync_SplitsLongTextByCharacterCount()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "long.txt"), new string('あ', 7000), Encoding.UTF8);
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(manualFolder, aiIndexFolder);

        Assert.True(result.IndexedChunkCount >= 3);
        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.All(document.Manuals, manual => Assert.True(manual.Text.Length <= 2600));
    }

    [Fact]
    public async Task BuildAsync_DoesNotCreateBlankOnlyChunks()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "blank.txt"), " \r\n\t ", Encoding.UTF8);
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(manualFolder, aiIndexFolder);

        Assert.Equal(0, result.IndexedFileCount);
        Assert.Equal(0, result.IndexedChunkCount);
        Assert.Equal(1, result.EmptyFileSkippedCount);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task BuildAsync_WritesManualsIndexUnderAiIndexFolder()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "manual.txt"), "本文", Encoding.UTF8);
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(manualFolder, aiIndexFolder);

        Assert.Equal(Path.Combine(aiIndexFolder, AiManualIndexBuilder.IndexFileName), result.IndexFilePath);
        Assert.True(File.Exists(result.IndexFilePath));
    }

    [Fact]
    public async Task BuildAsync_DoesNotModifyInputFile()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        var filePath = Path.Combine(manualFolder, "manual.txt");
        await File.WriteAllTextAsync(filePath, "既存マニュアル本文", Encoding.UTF8);
        var expectedLastWriteTime = new DateTime(2026, 6, 3, 9, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(filePath, expectedLastWriteTime);
        var builder = CreateBuilder();

        _ = await builder.BuildAsync(manualFolder, aiIndexFolder);

        Assert.Equal("既存マニュアル本文", await File.ReadAllTextAsync(filePath, Encoding.UTF8));
        Assert.Equal(expectedLastWriteTime, File.GetLastWriteTime(filePath));
    }

    [Fact]
    public async Task BuildAsync_PreservesJapaneseText()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "日本語.md"), "# 日本語見出し\r\nプリンター設定を確認します。", Encoding.UTF8);
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(manualFolder, aiIndexFolder);

        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.Contains(document.Manuals, manual => manual.SectionTitle == "日本語見出し");
        Assert.Contains(document.Manuals, manual => manual.Text.Contains("プリンター設定", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_IgnoresUnsupportedExtensions()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "manual.pdf"), "対象外", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "manual.html"), "対象外", Encoding.UTF8);
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(manualFolder, aiIndexFolder);

        Assert.Equal(0, result.IndexedFileCount);
        Assert.Equal(0, result.IndexedChunkCount);
        Assert.Equal(2, result.ScannedFileCount);
        Assert.Equal(0, result.SupportedFileCount);
        Assert.Equal(2, result.UnsupportedFileCount);
        Assert.Equal(1, result.UnsupportedDocumentFileCount);
        Assert.Equal(1, result.OtherUnsupportedFileCount);
        Assert.Equal(1, result.UnsupportedExtensionCounts[".pdf"]);
        Assert.Equal(1, result.UnsupportedExtensionCounts[".html"]);
        Assert.Equal(1, result.UnsupportedDocumentExtensionCounts[".pdf"]);
    }

    [Fact]
    public async Task BuildAsync_IndexesTxtAndMarkdownRecursively()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var subFolder = Path.Combine(manualFolder, "sub");
        var nestedFolder = Path.Combine(subFolder, "nested");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(nestedFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "root.txt"), "root text", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(subFolder, "sub.txt"), "sub text", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(nestedFolder, "nested.md"), "# Nested\r\nnested markdown", Encoding.UTF8);
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(manualFolder, aiIndexFolder);

        Assert.Equal(3, result.ScannedFileCount);
        Assert.Equal(3, result.SupportedFileCount);
        Assert.Equal(3, result.IndexedFileCount);
        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.Contains(document.Manuals, manual => manual.FileName == "root.txt");
        Assert.Contains(document.Manuals, manual => manual.FileName == "sub.txt");
        Assert.Contains(document.Manuals, manual => manual.FileName == "nested.md");
    }

    [Fact]
    public async Task BuildAsync_CountsUnsupportedOfficeImageAndArchiveFilesWithoutIndexing()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        foreach (var extension in new[] { ".pdf", ".docx", ".xlsx", ".png", ".zip" })
        {
            await File.WriteAllTextAsync(Path.Combine(manualFolder, $"unsupported{extension}"), "unsupported", Encoding.UTF8);
        }

        var result = await CreateBuilder().BuildAsync(manualFolder, aiIndexFolder);

        Assert.Equal(5, result.ScannedFileCount);
        Assert.Equal(5, result.UnsupportedFileCount);
        Assert.Equal(4, result.UnsupportedDocumentFileCount);
        Assert.Equal(1, result.OutOfScopeFileCount);
        Assert.Equal(0, result.IndexedFileCount);
        Assert.Equal(1, result.UnsupportedExtensionCounts[".pdf"]);
        Assert.Equal(1, result.UnsupportedExtensionCounts[".docx"]);
        Assert.Equal(1, result.UnsupportedExtensionCounts[".xlsx"]);
        Assert.Equal(1, result.UnsupportedExtensionCounts[".png"]);
        Assert.Equal(1, result.UnsupportedExtensionCounts[".zip"]);
        Assert.Equal(1, result.UnsupportedDocumentExtensionCounts[".pdf"]);
        Assert.Equal(1, result.UnsupportedDocumentExtensionCounts[".docx"]);
        Assert.Equal(1, result.UnsupportedDocumentExtensionCounts[".xlsx"]);
        Assert.Equal(1, result.UnsupportedDocumentExtensionCounts[".png"]);
        Assert.Equal(1, result.OutOfScopeExtensionCounts[".zip"]);
    }

    [Fact]
    public async Task BuildAsync_ExcludesPowerShellAndCommandLogsFromTextManuals()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(
            Path.Combine(manualFolder, "command_log.txt"),
            """
            Windows PowerShell Copyright (C) Microsoft Corporation. All rights reserved.
            PS C:\Work> CCT_Generator.exe --help
            PS C:\Work> CCT_Generator.exe --version
            No framework installation found.
            """,
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(manualFolder, "license_manual.md"),
            "# ライセンス認証エラー対応手順\r\nライセンスサーバー名とポート番号を確認します。",
            Encoding.UTF8);

        var result = await CreateBuilder().BuildAsync(manualFolder, aiIndexFolder);

        Assert.Equal(2, result.ScannedFileCount);
        Assert.Equal(2, result.SupportedFileCount);
        Assert.Equal(1, result.ContentExcludedFileCount);
        Assert.Equal(1, result.IndexedFileCount);
        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.Single(document.Manuals);
        Assert.Contains("ライセンス認証エラー", document.Manuals[0].Text);
    }

    [Fact]
    public async Task BuildManyAsync_DoesNotRegisterDuplicateFilesFromOverlappingFolders()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var subFolder = Path.Combine(manualFolder, "sub");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(subFolder);
        await File.WriteAllTextAsync(Path.Combine(subFolder, "shared.md"), "# Shared\r\nsame file", Encoding.UTF8);

        var result = await CreateBuilder().BuildManyAsync([manualFolder, subFolder], aiIndexFolder);

        Assert.Equal(1, result.IndexedFileCount);
        Assert.Equal(1, result.DuplicateFileSkippedCount);
        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.Single(document.Manuals);
    }

    private static AiManualIndexBuilder CreateBuilder()
    {
        return new AiManualIndexBuilder(() => new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.FromHours(9)));
    }

    private static async Task<AiManualIndexDocument> ReadIndexAsync(string indexFilePath)
    {
        await using var stream = File.OpenRead(indexFilePath);
        return await JsonSerializer.DeserializeAsync<AiManualIndexDocument>(stream)
            ?? throw new InvalidOperationException("Manual index JSON could not be deserialized.");
    }
}
