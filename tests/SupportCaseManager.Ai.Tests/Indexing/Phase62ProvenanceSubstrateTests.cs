using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Indexing;

public sealed class Phase62ProvenanceSubstrateTests
{
    [Fact]
    public void LogicalSourceLocator_IsStableAcrossMachineRoots_AndRejectsEscapes()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        var firstRoot = Path.Combine(first.Path, "manual-corpus");
        var secondRoot = Path.Combine(second.Path, "manual-corpus");
        Directory.CreateDirectory(Path.Combine(firstRoot, "guides"));
        Directory.CreateDirectory(Path.Combine(secondRoot, "guides"));
        var firstSource = Path.Combine(firstRoot, "guides", "setup.txt");
        var secondSource = Path.Combine(secondRoot, "guides", "setup.txt");

        Assert.True(LogicalSourceLocator.TryCreateManual(firstRoot, firstSource, out var firstLocator));
        Assert.True(LogicalSourceLocator.TryCreateManual(secondRoot, secondSource, out var secondLocator));
        Assert.Equal(firstLocator.Value, secondLocator.Value);
        Assert.Equal(
            LogicalSourceLocator.CreateLogicalSourceId("HelixQAC", "Manual", firstLocator),
            LogicalSourceLocator.CreateLogicalSourceId("HelixQAC", "Manual", secondLocator));
        Assert.DoesNotContain(first.Path, firstLocator.Value, StringComparison.OrdinalIgnoreCase);

        var prefix = firstLocator.Value[..(firstLocator.Value.LastIndexOf('/') + 1)];
        Assert.False(LogicalSourceLocator.TryResolveManual(firstRoot, new LogicalSourceLocator { Value = prefix + "../outside.txt" }, out _));
        Assert.False(LogicalSourceLocator.TryResolveManual(firstRoot, new LogicalSourceLocator { Value = prefix + "C:%5CWindows%5Csystem.ini" }, out _));
        Assert.True(LogicalSourceLocator.TryResolveManual(firstRoot, firstLocator, out var resolved));
        Assert.Equal(Path.GetFullPath(firstSource), resolved);
    }

    [Fact]
    public async Task ManualBuilder_WritesAdditiveProvenanceAndSidecars()
    {
        using var temp = new TempDirectory();
        var corpusRoot = Path.Combine(temp.Path, "manuals");
        var indexFolder = Path.Combine(temp.Path, "index");
        Directory.CreateDirectory(corpusRoot);
        await File.WriteAllTextAsync(Path.Combine(corpusRoot, "guide.txt"), new string('a', 3000), Encoding.UTF8);

        var result = await new AiManualIndexBuilder().BuildAsync(corpusRoot, indexFolder);
        var index = await ReadAsync<AiManualIndexDocument>(result.IndexFilePath);
        var chunks = index.Manuals.OrderBy(static item => item.ChunkLocator!.ChunkOrdinal).ToList();

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, chunk =>
        {
            Assert.False(string.IsNullOrWhiteSpace(chunk.LogicalSourceId));
            Assert.NotNull(chunk.LogicalSourceLocator);
            Assert.DoesNotContain(temp.Path, chunk.LogicalSourceLocator!.Value, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(chunk.ParsedSourceAddress);
            Assert.NotNull(chunk.ChunkLocator);
            Assert.Equal(Hash(chunk.Text), chunk.ChunkLocator!.ContentHash);
            Assert.Equal(ReadOnlyIndexRecordResolver.CreateIndexLookupKey("Manual", chunk.Id), chunk.IndexLookupKey);
        });
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(static item => item.ChunkLocator!.ChunkOrdinal));

        var registry = await ReadAsync<SourceRegistryDocument>(Path.Combine(indexFolder, SourceRegistryDocument.FileName));
        var artifacts = await ReadAsync<ParsedSourceArtifactDocument>(Path.Combine(indexFolder, ParsedSourceArtifactDocument.FileName));
        Assert.Single(registry.Sources);
        Assert.Single(artifacts.Sources);
        Assert.Contains("aaa", artifacts.Sources[0].Pages.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyResolver_ResolvesNewRecordWithoutChangingIndexFile()
    {
        using var temp = new TempDirectory();
        var corpusRoot = Path.Combine(temp.Path, "manuals");
        var indexFolder = Path.Combine(temp.Path, "index");
        Directory.CreateDirectory(corpusRoot);
        await File.WriteAllTextAsync(Path.Combine(corpusRoot, "guide.txt"), "qacli analyze project.qaf", Encoding.UTF8);
        var build = await new AiManualIndexBuilder().BuildAsync(corpusRoot, indexFolder);
        var index = await ReadAsync<AiManualIndexDocument>(build.IndexFilePath);
        var source = Assert.Single(index.Manuals);
        var before = await File.ReadAllBytesAsync(build.IndexFilePath);

        var resolution = await new ReadOnlyIndexRecordResolver().ResolveByIndexLookupKeyAsync(indexFolder, source.IndexLookupKey!);

        Assert.Equal(IndexRecordResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(source.Id, resolution.Record!.SourceId);
        Assert.Equal(source.ChunkLocator!.ChunkOrdinal, resolution.Record.ChunkLocator!.ChunkOrdinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(build.IndexFilePath));
    }

    [Fact]
    public async Task ParsedSourceSpanResolver_ReconstructsValidSpanAndRejectsMismatch()
    {
        using var temp = new TempDirectory();
        var corpusRoot = Path.Combine(temp.Path, "manuals");
        var indexFolder = Path.Combine(temp.Path, "index");
        Directory.CreateDirectory(corpusRoot);
        await File.WriteAllTextAsync(Path.Combine(corpusRoot, "guide.txt"), "qacli analyze project.qaf", Encoding.UTF8);
        var build = await new AiManualIndexBuilder().BuildAsync(corpusRoot, indexFolder);
        var index = await ReadAsync<AiManualIndexDocument>(build.IndexFilePath);
        var source = Assert.Single(index.Manuals);
        var resolver = new ParsedSourceSpanResolver();

        var matched = await resolver.ResolveAsync(indexFolder, source.ParsedSourceAddress, source.ChunkLocator);
        Assert.Equal(ParsedSourceSpanResolutionStatus.Matched, matched.Status);
        Assert.Equal(source.Text, matched.Text);

        var mismatched = await resolver.ResolveAsync(
            indexFolder,
            source.ParsedSourceAddress,
            source.ChunkLocator! with { ContentHash = "not-a-hash" });
        Assert.Equal(ParsedSourceSpanResolutionStatus.HashMismatch, mismatched.Status);
    }

    [Fact]
    public async Task ReadOnlyResolver_ReportsLegacyAndAmbiguousRecordsExplicitly()
    {
        using var temp = new TempDirectory();
        var legacyFolder = Path.Combine(temp.Path, "legacy");
        Directory.CreateDirectory(legacyFolder);
        await WriteAsync(Path.Combine(legacyFolder, AiManualIndexBuilder.IndexFileName), new AiManualIndexDocument
        {
            Manuals = [new AiIndexedManual { Id = "legacy-id", DocumentId = "legacy-document", ChunkId = "legacy-id" }],
        });

        var resolver = new ReadOnlyIndexRecordResolver();
        var legacy = await resolver.ResolveByIndexLookupKeyAsync(legacyFolder, "manual:legacy-id");
        Assert.Equal(IndexRecordResolutionStatus.LegacyProvenanceIncomplete, legacy.Status);

        var ambiguousFolder = Path.Combine(temp.Path, "ambiguous");
        Directory.CreateDirectory(ambiguousFolder);
        await WriteAsync(Path.Combine(ambiguousFolder, AiManualIndexBuilder.IndexFileName), new AiManualIndexDocument
        {
            Manuals =
            [
                new AiIndexedManual { Id = "duplicate", DocumentId = "one", ChunkId = "duplicate" },
                new AiIndexedManual { Id = "duplicate", DocumentId = "two", ChunkId = "duplicate" },
            ],
        });
        var ambiguous = await resolver.ResolveByIndexLookupKeyAsync(ambiguousFolder, "manual:duplicate");
        Assert.Equal(IndexRecordResolutionStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(IndexRecordResolutionStatus.NotFound, (await resolver.ResolveByIndexLookupKeyAsync(ambiguousFolder, "manual:missing")).Status);
        Assert.Equal(IndexRecordResolutionStatus.InvalidLookupKey, (await resolver.ResolveByIndexLookupKeyAsync(ambiguousFolder, "../manual:duplicate")).Status);
    }

    [Fact]
    public async Task ProvenanceFields_DoNotChangeManualKeywordSearchOrderOrScore()
    {
        using var temp = new TempDirectory();
        var indexFolder = Path.Combine(temp.Path, "index");
        Directory.CreateDirectory(indexFolder);
        var legacy = new AiIndexedManual
        {
            Id = "stable-id",
            FilePath = "legacy.txt",
            FileName = "legacy.txt",
            Title = "Analysis guide",
            Text = "qacli analyze project.qaf",
            DocumentId = "legacy.txt",
            ChunkId = "stable-id",
        };
        await WriteAsync(Path.Combine(indexFolder, AiManualIndexBuilder.IndexFileName), new AiManualIndexDocument { Manuals = [legacy] });
        var searcher = new AiManualKeywordSearcher();
        var before = await searcher.SearchAsync(indexFolder, "qacli analyze", 5);

        var locator = new LogicalSourceLocator { Value = "manual://root-0123456789abcdef/legacy.txt" };
        var enriched = legacy with
        {
            LogicalSourceId = LogicalSourceLocator.CreateLogicalSourceId("HelixQAC", "Manual", locator),
            LogicalSourceLocator = locator,
            ChunkLocator = new ChunkLocator
            {
                LogicalSourceId = LogicalSourceLocator.CreateLogicalSourceId("HelixQAC", "Manual", locator),
                ChunkOrdinal = 0,
                OffsetBasis = "ParsedSourceTextUtf16",
                Length = legacy.Text.Length,
                ContentHash = Hash(legacy.Text),
            },
            IndexLookupKey = ReadOnlyIndexRecordResolver.CreateIndexLookupKey("Manual", legacy.Id),
        };
        await WriteAsync(Path.Combine(indexFolder, AiManualIndexBuilder.IndexFileName), new AiManualIndexDocument { Manuals = [enriched] });
        var after = await searcher.SearchAsync(indexFolder, "qacli analyze", 5);

        Assert.Equal(before.Select(static source => source.SourceId), after.Select(static source => source.SourceId));
        Assert.Equal(before.Select(static source => source.Score), after.Select(static source => source.Score));
    }

    [Fact]
    public async Task OfficialBuilder_WritesDistinctChunkLocatorsAndRegistry()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"<html><head><title>Reference</title></head><body><h1>Commands</h1><p>{new string('x', 3200)}</p></body></html>", Encoding.UTF8, "text/html"),
        });
        var build = await new AiOfficialDocumentIndexBuilder(handler).BuildAsync(
            new ProductKnowledgeSettings { ProductName = "Checkmarx", DocumentUrls = ["https://docs.example.test/commands"] },
            Path.Combine(temp.Path, "index"));
        var index = await ReadAsync<AiOfficialDocumentIndexDocument>(build.IndexFilePath);

        Assert.True(index.Documents.Count >= 2);
        Assert.All(index.Documents, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.LogicalSourceId));
            Assert.Equal(item.Id, item.ChunkId);
            Assert.NotNull(item.ChunkLocator);
            Assert.Equal(ReadOnlyIndexRecordResolver.CreateIndexLookupKey("OfficialDoc", item.Id), item.IndexLookupKey);
        });
        Assert.Equal(index.Documents.Count, index.Documents.Select(static item => item.ChunkLocator!.ChunkOrdinal).Distinct().Count());
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(build.IndexFilePath)!, SourceRegistryDocument.FileName)));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(build.IndexFilePath)!, ParsedSourceArtifactDocument.FileName)));
    }

    private static async Task<T> ReadAsync<T>(string path)
        where T : class
    {
        await using var stream = File.OpenRead(path);
        return (await JsonSerializer.DeserializeAsync<T>(stream))!;
    }

    private static async Task WriteAsync<T>(string path, T value)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
