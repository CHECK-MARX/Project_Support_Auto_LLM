using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Indexing;

public sealed class SafeZipManualIndexTests
{
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("/absolute/manual.txt")]
    [InlineData("C:/absolute/manual.txt")]
    [InlineData("\\\\server\\share\\manual.txt")]
    public void IsSafeEntryPath_RejectsTraversalAndAbsolutePaths(string entryPath)
    {
        Assert.False(SafeZipManualReader.IsSafeEntryPath(entryPath));
    }

    [Fact]
    public async Task BuildAsync_IndexesPdfEntryWithArchiveMetadataWithoutChangingSourceZip()
    {
        using var temp = new TempDirectory();
        var (manualFolder, indexFolder) = CreateFolders(temp.Path);
        var zipPath = Path.Combine(manualFolder, "manuals.zip");
        CreateZip(zipPath, ("docs/manual.pdf", CreateSimplePdf("License setup manual")));
        var originalHash = ComputeHash(zipPath);

        var result = await CreateBuilder(temp.Path).BuildAsync(manualFolder, indexFolder);

        var document = await ReadIndexAsync(result.IndexFilePath);
        var manual = Assert.Single(document.Manuals);
        Assert.Equal(AiManualIndexDocument.CurrentVersion, document.Version);
        Assert.Equal(1, result.IndexedZipEntryCount);
        Assert.Equal("Manual", manual.SourceType);
        Assert.Equal(Path.GetFullPath(zipPath), manual.ArchivePath);
        Assert.Equal("docs/manual.pdf", manual.EntryPath);
        Assert.Equal("manual.pdf", manual.OriginalFileName);
        Assert.Equal(".pdf", manual.Extension);
        Assert.Equal(64, manual.Sha256?.Length);
        Assert.Contains("manuals.zip!/docs/manual.pdf", manual.FilePath, StringComparison.Ordinal);
        Assert.Equal(originalHash, ComputeHash(zipPath));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(temp.Path, "zip-temp")));
    }

    [Fact]
    public async Task BuildAsync_IndexesDocxEntry()
    {
        using var temp = new TempDirectory();
        var (manualFolder, indexFolder) = CreateFolders(temp.Path);
        var docx = CreateOfficePackage(
            "word/document.xml",
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Configuration guide text</w:t></w:r></w:p></w:body></w:document>");
        CreateZip(Path.Combine(manualFolder, "documents.zip"), ("guide/Configuration Guide.docx", docx));

        var result = await CreateBuilder(temp.Path).BuildAsync(manualFolder, indexFolder);

        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.Contains(document.Manuals, item => item.DocumentType == "Word" && item.Text.Contains("Configuration guide text", StringComparison.Ordinal));
        Assert.Equal(1, result.IndexedZipEntryCount);
    }

    [Fact]
    public async Task BuildAsync_SkipsTraceLogInsideZip()
    {
        using var temp = new TempDirectory();
        var (manualFolder, indexFolder) = CreateFolders(temp.Path);
        CreateZip(Path.Combine(manualFolder, "logs.zip"), ("trace/session.log", Encoding.UTF8.GetBytes("2026-08-09 INFO raw trace")));

        var result = await CreateBuilder(temp.Path).BuildAsync(manualFolder, indexFolder);

        Assert.Equal(1, result.ZipEntryCount);
        Assert.Equal(0, result.SupportedZipEntryCount);
        Assert.Equal(1, result.SkippedZipEntryCount);
        Assert.Equal(0, result.IndexedFileCount);
    }

    [Fact]
    public async Task BuildAsync_DoesNotExtractOrExecuteExecutableEntry()
    {
        using var temp = new TempDirectory();
        var (manualFolder, indexFolder) = CreateFolders(temp.Path);
        CreateZip(Path.Combine(manualFolder, "tools.zip"), ("bin/setup.exe", [0x4d, 0x5a, 0x00, 0x00]));

        var result = await CreateBuilder(temp.Path).BuildAsync(manualFolder, indexFolder);

        Assert.Equal(1, result.SkippedZipEntryCount);
        Assert.Equal(0, result.IndexedFileCount);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(temp.Path, "zip-temp")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "zip-temp", "setup.exe")));
    }

    [Fact]
    public async Task BuildAsync_RejectsZipSlipPath()
    {
        using var temp = new TempDirectory();
        var (manualFolder, indexFolder) = CreateFolders(temp.Path);
        CreateZip(Path.Combine(manualFolder, "unsafe.zip"), ("../outside.txt", Encoding.UTF8.GetBytes("manual text")));

        var result = await CreateBuilder(temp.Path).BuildAsync(manualFolder, indexFolder);

        Assert.Equal(1, result.UnsafeArchivePathRejectedCount);
        Assert.Equal(0, result.IndexedFileCount);
        Assert.False(File.Exists(Path.Combine(temp.Path, "outside.txt")));
    }

    [Fact]
    public async Task BuildAsync_PrefersExternalFileWhenArchiveContainsSameContent()
    {
        using var temp = new TempDirectory();
        var (manualFolder, indexFolder) = CreateFolders(temp.Path);
        var bytes = CreateSimplePdf("External manual is preferred");
        var externalPath = Path.Combine(manualFolder, "external_manual.pdf");
        await File.WriteAllBytesAsync(externalPath, bytes);
        CreateZip(Path.Combine(manualFolder, "duplicate.zip"), ("docs/manual.pdf", bytes));

        var result = await CreateBuilder(temp.Path).BuildAsync(manualFolder, indexFolder);

        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.All(document.Manuals, item => Assert.Equal(Path.GetFullPath(externalPath), item.FilePath));
        Assert.Equal(1, result.IndexedFileCount);
        Assert.Equal(1, result.DuplicateZipEntryCount);
        Assert.Contains(result.Diagnostics, item => item.Contains("Duplicate skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_SkipsCorruptZipAndContinuesWithRegularManual()
    {
        using var temp = new TempDirectory();
        var (manualFolder, indexFolder) = CreateFolders(temp.Path);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "broken.zip"), "not a zip", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "setup_manual.txt"), "Setup procedure is described here.", Encoding.UTF8);

        var result = await CreateBuilder(temp.Path).BuildAsync(manualFolder, indexFolder);

        Assert.Equal(1, result.CorruptZipCount);
        Assert.Equal(1, result.IndexedFileCount);
        Assert.Contains(result.Warnings, item => item.Contains("Corrupt ZIP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_SkipsEncryptedZipAndContinuesWithRegularManual()
    {
        using var temp = new TempDirectory();
        var (manualFolder, indexFolder) = CreateFolders(temp.Path);
        var zipPath = Path.Combine(manualFolder, "encrypted.zip");
        CreateZip(zipPath, ("secret.txt", Encoding.UTF8.GetBytes("secret manual")));
        MarkZipEntriesEncrypted(zipPath);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "guide.txt"), "The normal guide remains available.", Encoding.UTF8);

        var result = await CreateBuilder(temp.Path).BuildAsync(manualFolder, indexFolder);

        Assert.Equal(1, result.EncryptedZipCount);
        Assert.Equal(1, result.IndexedFileCount);
        Assert.Contains(result.Warnings, item => item.Contains("Encrypted ZIP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_StopsArchiveThatExceedsEntryCountLimit()
    {
        using var temp = new TempDirectory();
        var (manualFolder, indexFolder) = CreateFolders(temp.Path);
        CreateZip(
            Path.Combine(manualFolder, "many.zip"),
            ("one.txt", Encoding.UTF8.GetBytes("one manual")),
            ("two.txt", Encoding.UTF8.GetBytes("two manual")),
            ("three.txt", Encoding.UTF8.GetBytes("three manual")));
        var reader = new SafeZipManualReader(new SafeZipManualReaderOptions
        {
            MaxEntries = 2,
            TemporaryRootPath = Path.Combine(temp.Path, "zip-temp"),
        });

        var result = await new AiManualIndexBuilder(safeZipReader: reader).BuildAsync(manualFolder, indexFolder);

        Assert.Equal(1, result.ArchiveSizeLimitExceededCount);
        Assert.Equal(3, result.SkippedZipEntryCount);
        Assert.Equal(0, result.IndexedFileCount);
    }

    [Fact]
    public async Task BuildAsync_IndexesInstallationNotesPdfContainingCommands()
    {
        using var temp = new TempDirectory();
        var (manualFolder, indexFolder) = CreateFolders(temp.Path);
        CreateZip(
            Path.Combine(manualFolder, "release.zip"),
            ("Installation Notes.pdf", CreateSimplePdf("Run setup.exe quiet, configure.exe apply, and validate.exe check. Verify the installation result.")));

        var result = await CreateBuilder(temp.Path).BuildAsync(manualFolder, indexFolder);

        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.Contains(document.Manuals, item => item.FileName == "Installation Notes.pdf" && item.Text.Contains("setup.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, result.IndexedZipEntryCount);
        Assert.Equal(1, result.CommandHeavyManualIncludedCount);
    }

    [Fact]
    public async Task BuildAsync_SkipsNestedZip()
    {
        using var temp = new TempDirectory();
        var (manualFolder, indexFolder) = CreateFolders(temp.Path);
        CreateZip(Path.Combine(manualFolder, "outer.zip"), ("nested/inner.zip", CreateOfficePackage("manual.txt", "nested manual")));

        var result = await CreateBuilder(temp.Path).BuildAsync(manualFolder, indexFolder);

        Assert.Equal(1, result.SkippedZipEntryCount);
        Assert.Equal(0, result.IndexedFileCount);
    }

    [Fact]
    public async Task BuildAsync_SkipsEntryThatExceedsCompressionRatioLimit()
    {
        using var temp = new TempDirectory();
        var (manualFolder, indexFolder) = CreateFolders(temp.Path);
        var zipPath = Path.Combine(manualFolder, "compressed.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("large_manual.txt", CompressionLevel.SmallestSize);
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(new string('A', 50_000)));
        }

        var reader = new SafeZipManualReader(new SafeZipManualReaderOptions
        {
            MaxCompressionRatio = 2,
            TemporaryRootPath = Path.Combine(temp.Path, "zip-temp"),
        });
        var result = await new AiManualIndexBuilder(safeZipReader: reader).BuildAsync(manualFolder, indexFolder);

        Assert.Equal(1, result.ArchiveSizeLimitExceededCount);
        Assert.Equal(0, result.IndexedFileCount);
    }

    private static (string ManualFolder, string IndexFolder) CreateFolders(string root)
    {
        var manualFolder = Path.Combine(root, "manuals");
        var indexFolder = Path.Combine(root, "index");
        Directory.CreateDirectory(manualFolder);
        Directory.CreateDirectory(Path.Combine(root, "zip-temp"));
        return (manualFolder, indexFolder);
    }

    private static AiManualIndexBuilder CreateBuilder(string root) =>
        new(
            () => new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(9)),
            new SafeZipManualReader(new SafeZipManualReaderOptions
            {
                TemporaryRootPath = Path.Combine(root, "zip-temp"),
            }));

    private static void CreateZip(string zipPath, params (string Name, byte[] Content)[] entries)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(content);
        }
    }

    private static byte[] CreateOfficePackage(string entryName, string content)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(content);
        }

        return memory.ToArray();
    }

    private static byte[] CreateSimplePdf(string text)
    {
        var escapedText = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
        var content = $"BT /F1 12 Tf 72 720 Td ({escapedText}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append(offset.ToString("0000000000")).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string ComputeHash(string filePath) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();

    private static void MarkZipEntriesEncrypted(string zipPath)
    {
        var bytes = File.ReadAllBytes(zipPath);
        for (var index = 0; index <= bytes.Length - 10; index++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4));
            var flagOffset = signature switch
            {
                0x04034b50 => index + 6,
                0x02014b50 => index + 8,
                _ => -1,
            };
            if (flagOffset >= 0)
            {
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(flagOffset, 2));
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(flagOffset, 2), (ushort)(flags | 0x0001));
            }
        }

        File.WriteAllBytes(zipPath, bytes);
    }

    private static async Task<AiManualIndexDocument> ReadIndexAsync(string indexFilePath)
    {
        await using var stream = File.OpenRead(indexFilePath);
        return await JsonSerializer.DeserializeAsync<AiManualIndexDocument>(stream)
            ?? throw new InvalidOperationException("Manual index JSON could not be deserialized.");
    }
}
