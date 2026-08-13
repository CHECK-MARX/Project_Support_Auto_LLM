using System.Security.Cryptography;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Tests.Indexing;

public sealed class LiveManualIndexV3RebuildTests
{
    [Fact]
    public async Task ActualManualFolders_RebuildVersion3WithoutModifyingSources()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("SCM_RUN_LIVE_INDEX_V3_REBUILD"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsPath = Environment.GetEnvironmentVariable("SCM_LIVE_SETTINGS_PATH") ??
            Path.Combine(localAppData, "SupportCaseManager", "ai-data", "settings.json");
        var settings = JsonSerializer.Deserialize<AiAssistantSettings>(
            await File.ReadAllTextAsync(settingsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidOperationException("AI settings could not be loaded.");
        var product = settings.Products.First(item =>
            string.Equals(item.ProductName, "HelixQAC", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(product.ManualFolders);
        Assert.All(product.ManualFolders, folder => Assert.True(Directory.Exists(folder), folder));

        var aiIndexFolder = string.IsNullOrWhiteSpace(settings.AiIndexFolder)
            ? Path.Combine(localAppData, "SupportCaseManager", "ai-index")
            : settings.AiIndexFolder;
        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        var indexPath = Path.Combine(productIndexFolder, AiManualIndexBuilder.IndexFileName);
        var manifestPath = Path.Combine(productIndexFolder, KnowledgeManifest.FileName);
        var reportPath = Environment.GetEnvironmentVariable("SCM_LIVE_V3_REPORT") ??
            Path.Combine(Path.GetTempPath(), "SupportCaseManager", "manual-index-v3-report.json");
        var workRoot = Path.Combine(
            Path.GetDirectoryName(reportPath)!,
            $"manual-index-v3-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
        var stagingFolder = Path.Combine(workRoot, "staging");
        var backupFolder = Path.Combine(workRoot, "backup");
        Directory.CreateDirectory(stagingFolder);
        Directory.CreateDirectory(backupFolder);

        var sourceSnapshotBefore = SnapshotSources(product.ManualFolders);
        var oldIndex = await ReadIndexIfExistsAsync(indexPath);
        BackupIfExists(indexPath, Path.Combine(backupFolder, AiManualIndexBuilder.IndexFileName));
        BackupIfExists(manifestPath, Path.Combine(backupFolder, KnowledgeManifest.FileName));

        var stagingResult = await new AiManualIndexBuilder().BuildManyAsync(
            product.ManualFolders,
            stagingFolder);
        Assert.Equal(0, stagingResult.ErrorCount);
        var stagedIndex = await ReadIndexIfExistsAsync(stagingResult.IndexFilePath) ??
            throw new InvalidOperationException("Staged manual index was not generated.");
        Assert.Equal(AiManualIndexDocument.CurrentVersion, stagedIndex.Version);

        var service = new ProductScopedIndexService(
            new NoOpCaseIndexBuilder(),
            new AiManualIndexBuilder());
        var update = await service.UpdateKnowledgeAsync(
            product,
            aiIndexFolder,
            KnowledgeUpdateScope.Manuals,
            forceRebuild: true);
        var actualResult = update.Manuals ??
            throw new InvalidOperationException("Manual rebuild result was not returned.");
        var actualIndex = await ReadIndexIfExistsAsync(indexPath) ??
            throw new InvalidOperationException("Actual manual index was not generated.");
        var sourceSnapshotAfter = SnapshotSources(product.ManualFolders);

        await WriteReportAsync(reportPath, new
        {
            Product = product.ProductName,
            product.ManualFolders,
            IndexPath = indexPath,
            BackupFolder = backupFolder,
            SourceFilesUnchanged = sourceSnapshotBefore.SequenceEqual(sourceSnapshotAfter),
            SourceFileCount = sourceSnapshotAfter.Count,
            OldIndex = IndexStats(oldIndex),
            NewIndex = IndexStats(actualIndex),
            Build = new
            {
                actualResult.ScannedFileCount,
                actualResult.SupportedFileCount,
                actualResult.IndexedFileCount,
                actualResult.IndexedChunkCount,
                actualResult.ErrorCount,
                WarningCount = actualResult.Warnings.Count,
                actualResult.PageNumberChunkCount,
                actualResult.SectionTitleChunkCount,
                actualResult.PageAndSectionChunkCount,
                actualResult.ZipDerivedChunkCount,
                actualResult.DuplicateFileSkippedCount,
                actualResult.DuplicateZipEntryCount,
            },
            Warnings = actualResult.Warnings,
        });

        Assert.Equal(0, actualResult.ErrorCount);
        Assert.Equal(AiManualIndexDocument.CurrentVersion, actualIndex.Version);
        Assert.Equal(stagedIndex.Manuals.Count, actualIndex.Manuals.Count);
        Assert.Equal(sourceSnapshotBefore, sourceSnapshotAfter);
        Assert.True(actualResult.PageNumberChunkCount > 0);
        Assert.All(actualIndex.Manuals, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.DocumentTitle));
            Assert.False(string.IsNullOrWhiteSpace(item.DocumentId));
            Assert.False(string.IsNullOrWhiteSpace(item.ChunkId));
            Assert.Equal(item.Sha256, item.ContentHash);
        });
    }

    private static IReadOnlyList<string> SnapshotSources(IEnumerable<string> folders)
    {
        return folders
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var file = new FileInfo(path);
                return $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{file.Attributes}";
            })
            .ToList();
    }

    private static object IndexStats(AiManualIndexDocument? index) => new
    {
        Version = index?.Version,
        Files = index?.Manuals.Select(static item => item.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0,
        Chunks = index?.Manuals.Count ?? 0,
        PageNumberChunks = index?.Manuals.Count(static item => item.PageNumber is > 0) ?? 0,
        SectionTitleChunks = index?.Manuals.Count(static item => !string.IsNullOrWhiteSpace(item.SectionTitle)) ?? 0,
        PageAndSectionChunks = index?.Manuals.Count(static item =>
            item.PageNumber is > 0 && !string.IsNullOrWhiteSpace(item.SectionTitle)) ?? 0,
        ZipDerivedChunks = index?.Manuals.Count(static item => !string.IsNullOrWhiteSpace(item.ArchivePath)) ?? 0,
        Digest = index is null ? null : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(index))),
    };

    private static async Task<AiManualIndexDocument?> ReadIndexIfExistsAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AiManualIndexDocument>(stream);
    }

    private static void BackupIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: false);
        }
    }

    private static async Task WriteReportAsync(string path, object report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class NoOpCaseIndexBuilder : IAiCaseIndexBuilder
    {
        public Task<AiCaseIndexBuildResult> BuildAsync(
            string sourceFolder,
            string aiIndexFolder,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The live v3 rebuild must not rebuild past cases.");
    }
}
