using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Core.Llm;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed class EmbeddingIndexUpdater
{
    private const int BatchSize = 16;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private readonly IOllamaEmbeddingClient embeddingClient;
    private readonly Func<DateTimeOffset> nowProvider;

    public EmbeddingIndexUpdater(
        IOllamaEmbeddingClient? embeddingClient = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        this.embeddingClient = embeddingClient ?? new OllamaEmbeddingClient();
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public async Task<EmbeddingIndexUpdateResult> UpdateAsync(
        string productName,
        string productIndexFolder,
        string endpoint,
        string embeddingModel,
        bool forceRebuild = false,
        CancellationToken cancellationToken = default)
    {
        var indexPath = Path.Combine(productIndexFolder, EmbeddingIndexDocument.FileName);
        EmbeddingIndexDocument? existing = null;
        try
        {
            existing = await LoadAsync(indexPath, cancellationToken);
            var canReuse = !forceRebuild &&
                existing is { SchemaVersion: EmbeddingIndexDocument.CurrentSchemaVersion } &&
                string.Equals(existing.EmbeddingModel, embeddingModel, StringComparison.OrdinalIgnoreCase);
            var existingByKey = canReuse
                ? existing!.Entries
                    .GroupBy(EntryKey, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal)
                : new Dictionary<string, EmbeddingIndexEntry>(StringComparer.Ordinal);
            var sources = await ReadSourcesAsync(productName, productIndexFolder, cancellationToken);
            var sourceKeys = sources.Select(SourceKey).ToHashSet(StringComparer.Ordinal);
            var output = new List<EmbeddingIndexEntry>();
            var pending = new List<EmbeddingSource>();
            var unchanged = 0;
            var changed = 0;
            var added = 0;

            foreach (var source in sources)
            {
                var key = SourceKey(source);
                if (existingByKey.TryGetValue(key, out var oldEntry) &&
                    string.Equals(oldEntry.ContentHash, source.ContentHash, StringComparison.Ordinal) &&
                    oldEntry.Vector.Count > 0)
                {
                    output.Add(oldEntry);
                    unchanged++;
                    continue;
                }

                pending.Add(source);
                if (existingByKey.ContainsKey(key))
                {
                    changed++;
                }
                else
                {
                    added++;
                }
            }

            var deleted = existingByKey.Keys.Count(key => !sourceKeys.Contains(key));
            for (var offset = 0; offset < pending.Count; offset += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = pending.Skip(offset).Take(BatchSize).ToList();
                var vectors = await embeddingClient.EmbedAsync(
                    endpoint,
                    embeddingModel,
                    batch.Select(static source => source.EmbeddingText).ToList(),
                    cancellationToken);
                for (var index = 0; index < batch.Count; index++)
                {
                    output.Add(new EmbeddingIndexEntry
                    {
                        SourceId = batch[index].SourceId,
                        SourceType = batch[index].SourceType,
                        ProductName = batch[index].ProductName,
                        ContentHash = batch[index].ContentHash,
                        Vector = vectors[index],
                    });
                }
            }

            var document = new EmbeddingIndexDocument
            {
                ProductName = productName,
                EmbeddingModel = embeddingModel,
                BuiltAt = nowProvider(),
                Entries = output.OrderBy(static entry => entry.SourceType, StringComparer.Ordinal)
                    .ThenBy(static entry => entry.SourceId, StringComparer.Ordinal)
                    .ToList(),
            };
            await SaveAtomicallyAsync(indexPath, document, cancellationToken);
            return new EmbeddingIndexUpdateResult
            {
                IsSuccess = true,
                EmbeddingModel = embeddingModel,
                IndexFilePath = indexPath,
                AddedCount = added,
                ChangedCount = changed,
                DeletedCount = deleted,
                UnchangedCount = unchanged,
                VectorCount = output.Count,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new EmbeddingIndexUpdateResult
            {
                IsSuccess = false,
                EmbeddingModel = existing?.EmbeddingModel ?? string.Empty,
                IndexFilePath = indexPath,
                VectorCount = existing?.Entries.Count ?? 0,
                Warning = $"Embedding update failed; keyword fallback remains available. {ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    public static async Task<EmbeddingIndexDocument?> LoadAsync(
        string indexPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(indexPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(indexPath);
            return await JsonSerializer.DeserializeAsync<EmbeddingIndexDocument>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<EmbeddingSource>> ReadSourcesAsync(
        string productName,
        string productIndexFolder,
        CancellationToken cancellationToken)
    {
        var sources = new List<EmbeddingSource>();
        var cases = await ReadJsonAsync<AiIndexDocument>(Path.Combine(productIndexFolder, AiCaseIndexBuilder.IndexFileName), cancellationToken);
        var manuals = await ReadJsonAsync<AiManualIndexDocument>(Path.Combine(productIndexFolder, AiManualIndexBuilder.IndexFileName), cancellationToken);
        var official = await ReadJsonAsync<AiOfficialDocumentIndexDocument>(Path.Combine(productIndexFolder, AiOfficialDocumentIndexBuilder.IndexFileName), cancellationToken);
        var answerPairs = await ReadJsonAsync<CaseAnswerPairIndexDocument>(Path.Combine(productIndexFolder, CaseAnswerPairIndexDocument.FileName), cancellationToken);
        sources.AddRange(cases?.Notes.Select(note => CreateSource(note.Id, "PastCaseNote", productName, note.Title, note.Text)) ?? []);
        sources.AddRange(manuals?.Manuals.Select(manual => CreateSource(manual.Id, "Manual", productName, manual.Title, manual.Text)) ?? []);
        sources.AddRange(official?.Documents.Select(document => CreateSource(document.Id, "OfficialDoc", productName, document.Title, document.Text)) ?? []);
        sources.AddRange(answerPairs?.Pairs.Select(pair => CreateSource(
            pair.Id,
            "ExactPastAnswer",
            string.IsNullOrWhiteSpace(pair.ProductName) ? productName : pair.ProductName,
            pair.QuestionText,
            pair.CustomerReplyText)) ?? []);
        return sources
            .GroupBy(SourceKey, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToList();
    }

    private static EmbeddingSource CreateSource(
        string sourceId,
        string sourceType,
        string productName,
        string title,
        string text)
    {
        var embeddingText = $"{title}\n{text}";
        return new EmbeddingSource(
            sourceId,
            sourceType,
            productName,
            embeddingText,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(embeddingText))).ToLowerInvariant());
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static async Task SaveAtomicallyAsync(
        string indexPath,
        EmbeddingIndexDocument document,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{indexPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, indexPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string SourceKey(EmbeddingSource source) => $"{source.SourceType}\n{source.SourceId}";

    private static string EntryKey(EmbeddingIndexEntry entry) => $"{entry.SourceType}\n{entry.SourceId}";

    private sealed record EmbeddingSource(
        string SourceId,
        string SourceType,
        string ProductName,
        string EmbeddingText,
        string ContentHash);
}
