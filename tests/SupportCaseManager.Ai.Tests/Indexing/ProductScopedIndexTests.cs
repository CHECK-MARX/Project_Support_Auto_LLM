using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Indexing;

public sealed class ProductScopedIndexTests
{
    [Fact]
    public async Task BuildManualIndexAsync_WritesManualsIndexUnderProductFolder()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        Directory.CreateDirectory(manualFolder);
        await File.WriteAllTextAsync(Path.Combine(manualFolder, "license.md"), "# License\r\nlicense server port", Encoding.UTF8);
        var service = CreateService();

        var result = await service.BuildManualIndexAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "HelixQAC",
                ManualFolders = [manualFolder],
            },
            Path.Combine(temp.Path, "ai-index"));

        var expectedProductFolder = Path.Combine(temp.Path, "ai-index", "products", "HelixQAC");
        Assert.Equal(Path.Combine(expectedProductFolder, AiManualIndexBuilder.IndexFileName), result.IndexFilePath);
        Assert.True(File.Exists(result.IndexFilePath));
        var document = await ReadManualIndexAsync(result.IndexFilePath);
        Assert.Single(document.Manuals);
    }

    [Fact]
    public async Task BuildManualIndexAsync_IndexesMultipleManualFolders()
    {
        using var temp = new TempDirectory();
        var manualFolder1 = Path.Combine(temp.Path, "manuals1");
        var manualFolder2 = Path.Combine(temp.Path, "manuals2");
        Directory.CreateDirectory(manualFolder1);
        Directory.CreateDirectory(manualFolder2);
        await File.WriteAllTextAsync(Path.Combine(manualFolder1, "license.md"), "# License\r\nlicense server", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(manualFolder2, "firewall.txt"), "firewall port", Encoding.UTF8);
        var service = CreateService();

        var result = await service.BuildManualIndexAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "HelixQAC",
                ManualFolders = [manualFolder1, manualFolder2],
            },
            Path.Combine(temp.Path, "ai-index"));

        Assert.Equal(2, result.IndexedFileCount);
        var document = await ReadManualIndexAsync(result.IndexFilePath);
        Assert.Contains(document.Manuals, manual => manual.FileName == "license.md");
        Assert.Contains(document.Manuals, manual => manual.FileName == "firewall.txt");
    }

    [Fact]
    public async Task BuildCaseIndexAsync_WritesCaseIndexUnderProductFolder()
    {
        using var temp = new TempDirectory();
        var sourceFolder = Path.Combine(temp.Path, "closed");
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var caseBuilder = new WritingCaseIndexBuilder();
        var service = new ProductScopedIndexService(caseBuilder, new AiManualIndexBuilder());

        var result = await service.BuildCaseIndexAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "Checkmarx",
                CloseFolder = sourceFolder,
            },
            aiIndexFolder);

        var expectedProductFolder = Path.Combine(aiIndexFolder, "products", "Checkmarx");
        Assert.Equal(Path.Combine(expectedProductFolder, AiCaseIndexBuilder.IndexFileName), result.IndexFilePath);
        Assert.True(File.Exists(result.IndexFilePath));
        Assert.Equal(sourceFolder, caseBuilder.LastSourceFolder);
    }

    [Fact]
    public void GetProductIndexFolder_SanitizesInvalidProductName()
    {
        using var temp = new TempDirectory();
        var service = CreateService();

        var folder = service.GetProductIndexFolder(temp.Path, "Product/Name:Beta?");

        Assert.StartsWith(Path.Combine(temp.Path, "products"), folder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('/', Path.GetFileName(folder));
        Assert.DoesNotContain(':', Path.GetFileName(folder));
        Assert.DoesNotContain('?', Path.GetFileName(folder));
    }

    [Fact]
    public async Task BuildManualIndexAsync_DoesNotModifySourceManualFiles()
    {
        using var temp = new TempDirectory();
        var manualFolder = Path.Combine(temp.Path, "manuals");
        Directory.CreateDirectory(manualFolder);
        var manualPath = Path.Combine(manualFolder, "manual.txt");
        await File.WriteAllTextAsync(manualPath, "original manual text", Encoding.UTF8);
        var expectedLastWriteTime = new DateTime(2026, 6, 4, 10, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(manualPath, expectedLastWriteTime);
        var service = CreateService();

        _ = await service.BuildManualIndexAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "Klocwork",
                ManualFolders = [manualFolder],
            },
            Path.Combine(temp.Path, "ai-index"));

        Assert.Equal("original manual text", await File.ReadAllTextAsync(manualPath, Encoding.UTF8));
        Assert.Equal(expectedLastWriteTime, File.GetLastWriteTime(manualPath));
    }

    private static ProductScopedIndexService CreateService()
    {
        return new ProductScopedIndexService(new WritingCaseIndexBuilder(), new AiManualIndexBuilder());
    }

    private static async Task<AiManualIndexDocument> ReadManualIndexAsync(string indexFilePath)
    {
        await using var stream = File.OpenRead(indexFilePath);
        return await JsonSerializer.DeserializeAsync<AiManualIndexDocument>(stream)
            ?? throw new InvalidOperationException("Manual index JSON could not be deserialized.");
    }

    private sealed class WritingCaseIndexBuilder : IAiCaseIndexBuilder
    {
        public string? LastSourceFolder { get; private set; }

        public async Task<AiCaseIndexBuildResult> BuildAsync(
            string sourceFolder,
            string aiIndexFolder,
            CancellationToken cancellationToken = default)
        {
            LastSourceFolder = sourceFolder;
            Directory.CreateDirectory(aiIndexFolder);
            var indexFilePath = Path.Combine(aiIndexFolder, AiCaseIndexBuilder.IndexFileName);
            await using var stream = File.Create(indexFilePath);
            await JsonSerializer.SerializeAsync(
                stream,
                new AiIndexDocument
                {
                    BuiltAt = new DateTimeOffset(2026, 6, 4, 10, 0, 0, TimeSpan.FromHours(9)),
                    SourceFolder = sourceFolder,
                    Notes = [],
                },
                cancellationToken: cancellationToken);

            return new AiCaseIndexBuildResult
            {
                IndexedCaseCount = 0,
                IndexedNoteCount = 0,
                IndexFilePath = indexFilePath,
            };
        }
    }
}
