using System.Text.Json;
using System.Net.Http.Json;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Indexing;

public sealed class EmbeddingIndexUpdaterTests
{
    [Fact]
    public async Task UpdateAsync_AddsChangesDeletesAndSkipsUnchangedVectors()
    {
        using var temp = new TempDirectory();
        var client = new RecordingEmbeddingClient();
        var updater = new EmbeddingIndexUpdater(client);
        await WriteManualIndexAsync(temp.Path,
        [
            CreateManual("manual-a", "alpha"),
            CreateManual("manual-b", "beta"),
        ]);

        var first = await updater.UpdateAsync("HelixQAC", temp.Path, "http://localhost:11434", "nomic-embed-text");
        var unchanged = await updater.UpdateAsync("HelixQAC", temp.Path, "http://localhost:11434", "nomic-embed-text");

        await WriteManualIndexAsync(temp.Path,
        [
            CreateManual("manual-a", "alpha changed"),
            CreateManual("manual-c", "gamma"),
        ]);
        var differential = await updater.UpdateAsync("HelixQAC", temp.Path, "http://localhost:11434", "nomic-embed-text");
        var index = await EmbeddingIndexUpdater.LoadAsync(Path.Combine(temp.Path, EmbeddingIndexDocument.FileName));

        Assert.True(first.IsSuccess);
        Assert.Equal(2, first.AddedCount);
        Assert.Equal(2, unchanged.UnchangedCount);
        Assert.Equal(1, differential.AddedCount);
        Assert.Equal(1, differential.ChangedCount);
        Assert.Equal(1, differential.DeletedCount);
        Assert.Equal(4, client.EmbeddedInputCount);
        Assert.NotNull(index);
        Assert.Equal(EmbeddingIndexDocument.CurrentSchemaVersion, index.SchemaVersion);
        Assert.Equal("Ollama", index.EmbeddingProvider);
        Assert.True(index.EmbeddingNormalized);
        Assert.Equal(2, index.EmbeddingDimension);
        Assert.All(index.Entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.ChunkContentHash)));
        Assert.Equal(2, index.Entries.Count);
        Assert.DoesNotContain(index.Entries, entry => entry.SourceId == "manual-b");
    }

    [Fact]
    public async Task UpdateAsync_WhenEmbeddingFails_PreservesPreviousIndex()
    {
        using var temp = new TempDirectory();
        var client = new RecordingEmbeddingClient();
        var updater = new EmbeddingIndexUpdater(client);
        await WriteManualIndexAsync(temp.Path, [CreateManual("manual-a", "alpha")]);
        var first = await updater.UpdateAsync("HelixQAC", temp.Path, "http://localhost:11434", "nomic-embed-text");
        var indexPath = Path.Combine(temp.Path, EmbeddingIndexDocument.FileName);
        var previousBytes = await File.ReadAllBytesAsync(indexPath);

        client.ThrowOnEmbed = true;
        await WriteManualIndexAsync(temp.Path, [CreateManual("manual-a", "changed")]);
        var failed = await updater.UpdateAsync("HelixQAC", temp.Path, "http://localhost:11434", "nomic-embed-text");

        Assert.True(first.IsSuccess);
        Assert.False(failed.IsSuccess);
        Assert.Contains("keyword fallback", failed.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(previousBytes, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal("Failed", failed.Status);
    }

    [Fact]
    public async Task StagingBuild_WritesOnlyStagingIndex_AndSanitizesEmbeddingInput()
    {
        using var source = new TempDirectory();
        using var staging = new TempDirectory();
        var client = new RecordingEmbeddingClient();
        var updater = new EmbeddingIndexUpdater(client);
        using var httpClient = new HttpClient(new TagsHandler());
        var builder = new EmbeddingIndexStagingBuilder(updater, httpClient);
        await WriteManualIndexAsync(source.Path,
        [
            CreateManual("manual-a", "担当者: 山田 太郎\nmail@example.test\nQAC analysis procedure"),
        ]);

        var result = await builder.BuildAsync(
            "HelixQAC",
            source.Path,
            staging.Path,
            "http://localhost:11434",
            "nomic-embed-text");
        var stagingFile = Path.Combine(staging.Path, "HelixQAC", EmbeddingIndexDocument.FileName);
        var validation = await EmbeddingIndexUpdater.ValidateAsync(
            stagingFile,
            "HelixQAC",
            source.Path,
            "nomic-embed-text");
        var index = await EmbeddingIndexUpdater.LoadAsync(stagingFile);

        Assert.True(result.IsSuccess);
        Assert.False(File.Exists(Path.Combine(source.Path, EmbeddingIndexDocument.FileName)));
        Assert.True(validation.IsValid);
        Assert.NotNull(index);
        Assert.Equal("sha256:test", index.EmbeddingModelDigest);
        Assert.Equal("cosine", index.DistanceMetric);
        Assert.True(index.Entries.Single().EmbeddingInputSanitized);
        Assert.DoesNotContain("mail@example.test", client.Inputs.Single(), StringComparison.OrdinalIgnoreCase);
    }

    private static AiIndexedManual CreateManual(string id, string text)
    {
        return new AiIndexedManual
        {
            Id = id,
            FilePath = Path.Combine("manuals", $"{id}.txt"),
            FileName = $"{id}.txt",
            Title = id,
            Text = text,
        };
    }

    private static async Task WriteManualIndexAsync(
        string productIndexFolder,
        IReadOnlyList<AiIndexedManual> manuals)
    {
        Directory.CreateDirectory(productIndexFolder);
        await using var stream = File.Create(Path.Combine(productIndexFolder, AiManualIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, new AiManualIndexDocument
        {
            BuiltAt = DateTimeOffset.Now,
            Manuals = manuals,
        });
    }

    private sealed class RecordingEmbeddingClient : IOllamaEmbeddingClient
    {
        public int EmbeddedInputCount { get; private set; }

        public bool ThrowOnEmbed { get; set; }

        public List<string> Inputs { get; } = [];

        public Task<IReadOnlyList<IReadOnlyList<float>>> EmbedAsync(
            string endpoint,
            string model,
            IReadOnlyList<string> inputs,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnEmbed)
            {
                throw new InvalidOperationException("embedding unavailable");
            }

            EmbeddedInputCount += inputs.Count;
            Inputs.AddRange(inputs);
            IReadOnlyList<IReadOnlyList<float>> vectors = inputs
                .Select((_, index) => (IReadOnlyList<float>)new[] { 1f, (float)(EmbeddedInputCount + index) })
                .ToList();
            return Task.FromResult(vectors);
        }
    }

    private sealed class TagsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { models = new[] { new { name = "nomic-embed-text", digest = "sha256:test" } } }),
            });
        }
    }
}
