using System.Text;
using System.Text.Json;
using System.IO.Compression;
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
    public async Task BuildAsync_IndexesPdfDocxHtmlAndCsvFiles()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        await WriteSimplePdfAsync(Path.Combine(manualFolder, "manual.pdf"), "License PDF Manual");
        WriteSingleEntryZip(
            Path.Combine(manualFolder, "manual.docx"),
            "word/document.xml",
            """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:p><w:r><w:t>Word manual text</w:t></w:r></w:p></w:body>
            </w:document>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(manualFolder, "guide.html"),
            "<html><body><h1>HTML manual</h1><p>Browser setup guide</p></body></html>",
            Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "table.csv"), "title,body\r\nCSV manual,Port setting", Encoding.UTF8);
        var builder = CreateBuilder();

        var result = await builder.BuildAsync(manualFolder, aiIndexFolder);

        Assert.Equal(4, result.ScannedFileCount);
        Assert.Equal(4, result.SupportedFileCount);
        Assert.Equal(4, result.IndexedFileCount);
        Assert.Equal(0, result.UnsupportedFileCount);
        var document = await ReadIndexAsync(result.IndexFilePath);
        var pdf = Assert.Single(document.Manuals, manual => manual.DocumentType == "Pdf");
        Assert.Contains("License PDF Manual", pdf.Text, StringComparison.Ordinal);
        Assert.Equal(1, pdf.PageNumber);
        Assert.False(string.IsNullOrWhiteSpace(pdf.ChunkId));
        Assert.Equal(AiManualIndexDocument.CurrentVersion, document.Version);
        Assert.Contains(document.Manuals, manual => manual.DocumentType == "Word" && manual.Text.Contains("Word manual text", StringComparison.Ordinal));
        Assert.Contains(document.Manuals, manual => manual.DocumentType == "Html" && manual.Text.Contains("Browser setup guide", StringComparison.Ordinal));
        Assert.Contains(document.Manuals, manual => manual.DocumentType == "Csv" && manual.Text.Contains("Port setting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_PreservesEveryPdfPageNumber()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        await WritePdfPagesAsync(
            Path.Combine(manualFolder, "multi-page.pdf"),
            "Preparation on the first page",
            "Analysis procedure on the second page");

        var result = await CreateBuilder().BuildAsync(manualFolder, aiIndexFolder);
        var document = await ReadIndexAsync(result.IndexFilePath);

        Assert.Equal(2, document.Manuals.Count);
        Assert.Collection(
            document.Manuals.OrderBy(static item => item.PageNumber),
            item =>
            {
                Assert.Equal(1, item.PageNumber);
                Assert.Contains("first page", item.Text, StringComparison.Ordinal);
            },
            item =>
            {
                Assert.Equal(2, item.PageNumber);
                Assert.Contains("second page", item.Text, StringComparison.Ordinal);
            });
        Assert.Equal(2, result.PageNumberChunkCount);
        Assert.Equal(0, result.SectionTitleChunkCount);
        Assert.Equal(0, result.PageAndSectionChunkCount);
    }

    [Fact]
    public async Task BuildAsync_PreservesDocxAndHtmlHeadingsAsV3Metadata()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        WriteSingleEntryZip(
            Path.Combine(manualFolder, "guide.docx"),
            "word/document.xml",
            """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:pPr><w:pStyle w:val="Heading1" /></w:pPr><w:r><w:t>プロジェクトの解析</w:t></w:r></w:p>
                <w:p><w:r><w:t>解析を実行します。</w:t></w:r></w:p>
              </w:body>
            </w:document>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(manualFolder, "guide.html"),
            "<html><body><h2>CLIでの解析</h2><p>qacli analyze を実行します。</p></body></html>",
            Encoding.UTF8);

        var result = await CreateBuilder().BuildAsync(manualFolder, aiIndexFolder);
        var document = await ReadIndexAsync(result.IndexFilePath);

        Assert.Equal(AiManualIndexDocument.CurrentVersion, document.Version);
        Assert.Contains(document.Manuals, item => item.DocumentType == "Word" && item.SectionTitle == "プロジェクトの解析");
        Assert.Contains(document.Manuals, item => item.DocumentType == "Html" && item.SectionTitle == "CLIでの解析");
        Assert.All(document.Manuals, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.DocumentTitle));
            Assert.False(string.IsNullOrWhiteSpace(item.DocumentId));
            Assert.Equal(item.Sha256, item.ContentHash);
            Assert.Equal(item.LastModifiedAt, item.SourceUpdatedAt);
        });
        Assert.Equal(document.Manuals.Count, result.SectionTitleChunkCount);
    }

    [Fact]
    public async Task OldIndexWithoutV3Metadata_RemainsDeserializable()
    {
        const string json = """
            { "version": 1, "builtAt": "2026-01-01T00:00:00Z", "sourceFolder": "manuals",
              "manuals": [ { "id": "old", "filePath": "old.pdf", "fileName": "old.pdf",
                "title": "Old Manual", "documentType": "Pdf", "sectionTitle": "", "text": "legacy" } ] }
            """;

        var restored = JsonSerializer.Deserialize<AiManualIndexDocument>(json);

        var manual = Assert.Single(restored!.Manuals);
        Assert.Equal("old", manual.Id);
        Assert.Null(manual.PageNumber);
        Assert.Null(manual.DocumentId);
        Assert.Null(manual.ContentHash);
    }

    [Fact]
    public void Version3Metadata_RoundTripsWithoutInventingUnavailableValues()
    {
        var document = new AiManualIndexDocument
        {
            Manuals =
            [
                new AiIndexedManual
                {
                    Id = "chunk-1",
                    ChunkId = "chunk-1",
                    DocumentId = "document-1",
                    DocumentTitle = "Guide",
                    FilePath = "guide.pdf",
                    FileName = "guide.pdf",
                    SourceType = "Manual",
                    DocumentType = "Pdf",
                    PageNumber = 7,
                    ContentHash = new string('a', 64),
                    Text = "verified text",
                },
            ],
        };

        var restored = JsonSerializer.Deserialize<AiManualIndexDocument>(JsonSerializer.Serialize(document));

        var manual = Assert.Single(restored!.Manuals);
        Assert.Equal(AiManualIndexDocument.CurrentVersion, restored.Version);
        Assert.Equal(7, manual.PageNumber);
        Assert.Equal("Guide", manual.DocumentTitle);
        Assert.Equal(string.Empty, manual.SectionTitle);
        Assert.Null(manual.Heading);
        Assert.Null(manual.Product);
        Assert.Null(manual.ProductVersion);
        Assert.Null(manual.Url);
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
    public async Task BuildAsync_CountsLegacyOfficeImageAndArchiveFilesWithoutIndexing()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        foreach (var extension in new[] { ".doc", ".xls", ".ppt", ".png", ".zip" })
        {
            await File.WriteAllTextAsync(Path.Combine(manualFolder, $"unsupported{extension}"), "unsupported", Encoding.UTF8);
        }

        var result = await CreateBuilder().BuildAsync(manualFolder, aiIndexFolder);

        Assert.Equal(5, result.ScannedFileCount);
        Assert.Equal(1, result.SupportedFileCount);
        Assert.Equal(4, result.UnsupportedFileCount);
        Assert.Equal(4, result.UnsupportedDocumentFileCount);
        Assert.Equal(0, result.OutOfScopeFileCount);
        Assert.Equal(1, result.ZipFileCount);
        Assert.Equal(1, result.CorruptZipCount);
        Assert.Equal(0, result.IndexedFileCount);
        Assert.Equal(1, result.UnsupportedExtensionCounts[".doc"]);
        Assert.Equal(1, result.UnsupportedExtensionCounts[".xls"]);
        Assert.Equal(1, result.UnsupportedExtensionCounts[".ppt"]);
        Assert.Equal(1, result.UnsupportedExtensionCounts[".png"]);
        Assert.Equal(1, result.UnsupportedDocumentExtensionCounts[".doc"]);
        Assert.Equal(1, result.UnsupportedDocumentExtensionCounts[".xls"]);
        Assert.Equal(1, result.UnsupportedDocumentExtensionCounts[".ppt"]);
        Assert.Equal(1, result.UnsupportedDocumentExtensionCounts[".png"]);
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

    [Fact]
    public async Task BuildManyIncrementalAsync_TracksAddedChangedUnchangedAndDeletedFiles()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        Directory.CreateDirectory(manualFolder);
        var firstPath = Path.Combine(manualFolder, "first.txt");
        var secondPath = Path.Combine(manualFolder, "second.md");
        await File.WriteAllTextAsync(firstPath, "first version", Encoding.UTF8);
        var builder = CreateBuilder();

        var initial = await builder.BuildManyIncrementalAsync([manualFolder], aiIndexFolder);
        var unchanged = await builder.BuildManyIncrementalAsync([manualFolder], aiIndexFolder);
        await File.WriteAllTextAsync(firstPath, "changed version", Encoding.UTF8);
        File.SetLastWriteTime(firstPath, File.GetLastWriteTime(firstPath).AddSeconds(2));
        var changed = await builder.BuildManyIncrementalAsync([manualFolder], aiIndexFolder);
        await File.WriteAllTextAsync(secondPath, "# Second\r\nsecond content", Encoding.UTF8);
        var added = await builder.BuildManyIncrementalAsync([manualFolder], aiIndexFolder);
        File.Delete(firstPath);
        var deleted = await builder.BuildManyIncrementalAsync([manualFolder], aiIndexFolder);

        Assert.Equal(1, initial.AddedFileCount);
        Assert.Equal(1, unchanged.UnchangedFileCount);
        Assert.Equal(1, changed.ChangedFileCount);
        Assert.Equal(1, added.AddedFileCount);
        Assert.Equal(1, deleted.DeletedFileCount);
        var document = await ReadIndexAsync(deleted.IndexFilePath);
        var remaining = Assert.Single(document.Manuals.Select(item => item.FileName).Distinct());
        Assert.Equal("second.md", remaining);
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

    private static void WriteSingleEntryZip(string filePath, string entryName, string content)
    {
        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content);
    }

    private static async Task WriteSimplePdfAsync(string filePath, string text)
    {
        await WritePdfPagesAsync(filePath, text);
    }

    private static async Task WritePdfPagesAsync(string filePath, params string[] pageTexts)
    {
        Assert.NotEmpty(pageTexts);
        var fontObjectNumber = 3 + (pageTexts.Length * 2);
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pageTexts.Length).Select(static index => $"{3 + (index * 2)} 0 R"))}] /Count {pageTexts.Length} >>",
        };
        foreach (var text in pageTexts)
        {
            var escapedText = text
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("(", "\\(", StringComparison.Ordinal)
                .Replace(")", "\\)", StringComparison.Ordinal);
            var content = $"BT /F1 12 Tf 72 720 Td ({escapedText}) Tj ET";
            var contentObjectNumber = objects.Count + 2;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontObjectNumber} 0 R >> >> /Contents {contentObjectNumber} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        var builder = new StringBuilder();
        var offsets = new List<int>();
        builder.Append("%PDF-1.4\n");
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1);
            builder.Append(" 0 obj\n");
            builder.Append(objects[index]);
            builder.Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n");
        builder.Append("0 ");
        builder.Append(objects.Count + 1);
        builder.Append("\n");
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(offset.ToString("0000000000"));
            builder.Append(" 00000 n \n");
        }

        builder.Append("trailer\n");
        builder.Append("<< /Size ");
        builder.Append(objects.Count + 1);
        builder.Append(" /Root 1 0 R >>\n");
        builder.Append("startxref\n");
        builder.Append(xrefOffset);
        builder.Append("\n%%EOF\n");

        await File.WriteAllTextAsync(filePath, builder.ToString(), Encoding.ASCII);
    }
}
