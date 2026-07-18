using System.Text.Json;
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
            IReadOnlyList<IReadOnlyList<float>> vectors = inputs
                .Select((_, index) => (IReadOnlyList<float>)new[] { 1f, (float)(EmbeddedInputCount + index) })
                .ToList();
            return Task.FromResult(vectors);
        }
    }
}
