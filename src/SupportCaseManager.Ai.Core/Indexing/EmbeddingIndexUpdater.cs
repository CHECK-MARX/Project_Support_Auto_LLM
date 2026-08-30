using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Llm;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed class EmbeddingIndexUpdater
{
    private const int BatchSize = 16;
    // Keep each batched request within the embedding model's effective context window.
    private const int MaxEmbeddingInputCharacters = 1600;
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
        CancellationToken cancellationToken = default,
        string? sourceProductIndexFolder = null,
        string? embeddingModelDigest = null,
        bool sanitizeEmbeddingInput = false)
    {
        var indexPath = Path.Combine(productIndexFolder, EmbeddingIndexDocument.FileName);
        EmbeddingIndexDocument? existing = null;
        try
        {
            existing = await LoadAsync(indexPath, cancellationToken);
            var canReuse = !forceRebuild &&
                existing is { SchemaVersion: EmbeddingIndexDocument.CurrentSchemaVersion } &&
                string.Equals(existing.EmbeddingProvider, "Ollama", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.EmbeddingModel, embeddingModel, StringComparison.OrdinalIgnoreCase) &&
                existing.EmbeddingNormalized;
            var existingByKey = canReuse
                ? existing!.Entries
                    .GroupBy(EntryKey, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal)
                : new Dictionary<string, EmbeddingIndexEntry>(StringComparer.Ordinal);
            var sources = await ReadSourcesAsync(
                productName,
                sourceProductIndexFolder ?? productIndexFolder,
                sanitizeEmbeddingInput,
                cancellationToken);
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
            var dimension = output.FirstOrDefault()?.Vector.Count ?? 0;
            var pendingGroups = pending
                .GroupBy(source => sanitizeEmbeddingInput ? source.EmbeddingGroupKey : SourceKey(source), StringComparer.Ordinal)
                .Select(static group => new EmbeddingGroup(
                    group.Key,
                    LimitEmbeddingInput(string.Join(Environment.NewLine, group.Select(static source => source.EmbeddingText))),
                    group.First().EmbeddingInputSanitized))
                .ToList();
            var vectorsByGroup = new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);
            var hashesByGroup = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var offset = 0; offset < pendingGroups.Count; offset += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = pendingGroups.Skip(offset).Take(BatchSize).ToList();
                var vectors = await embeddingClient.EmbedAsync(
                    endpoint,
                    embeddingModel,
                    batch.Select(static group => group.EmbeddingText).ToList(),
                    cancellationToken);
                for (var index = 0; index < batch.Count; index++)
                {
                    var vector = NormalizeVector(vectors[index]);
                    if (vector.Count == 0 || (dimension > 0 && vector.Count != dimension))
                    {
                        throw new InvalidOperationException("Ollama returned inconsistent embedding dimensions.");
                    }
                    dimension = vector.Count;
                    vectorsByGroup[batch[index].Key] = vector;
                    hashesByGroup[batch[index].Key] = Hash(batch[index].EmbeddingText);
                }
            }

            foreach (var source in pending)
            {
                var vectorKey = sanitizeEmbeddingInput ? source.EmbeddingGroupKey : SourceKey(source);
                var vector = vectorsByGroup[vectorKey];
                output.Add(new EmbeddingIndexEntry
                {
                    SourceId = source.SourceId,
                    SourceType = source.SourceType,
                    ProductName = source.ProductName,
                    ContentHash = source.ContentHash,
                    ChunkContentHash = source.ContentHash,
                    EmbeddingInputContentHash = hashesByGroup[vectorKey],
                    DocumentLocator = source.DocumentLocator,
                    EmbeddingInputSanitized = source.EmbeddingInputSanitized,
                    Vector = vector,
                });
            }

            var document = new EmbeddingIndexDocument
            {
                ProductName = productName,
                EmbeddingModel = embeddingModel,
                EmbeddingProvider = "Ollama",
                EmbeddingModelIdentifier = embeddingModel,
                EmbeddingModelDigest = embeddingModelDigest ?? string.Empty,
                EmbeddingDimension = dimension,
                EmbeddingNormalized = true,
                DistanceMetric = "cosine",
                BuiltAt = nowProvider(),
                CreatedAt = existing is { SchemaVersion: EmbeddingIndexDocument.CurrentSchemaVersion }
                    && existing.CreatedAt != default
                    ? existing.CreatedAt
                    : nowProvider(),
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
                EmbeddingDimension = dimension,
                Status = "Updated",
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
                Status = "Failed",
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

    public static async Task<EmbeddingIndexValidationResult> ValidateAsync(
        string indexPath,
        string productName,
        string sourceProductIndexFolder,
        string embeddingModel,
        CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(indexPath, cancellationToken);
        if (document is null)
        {
            return new EmbeddingIndexValidationResult { Message = "Embedding index was not found." };
        }

        var sources = await ReadSourcesAsync(
            productName,
            sourceProductIndexFolder,
            document.Entries.Any(static entry => entry.EmbeddingInputSanitized),
            cancellationToken);
        var sourceHashes = sources.ToDictionary(SourceKey, static source => source.ContentHash, StringComparer.Ordinal);
        var invalid = 0;
        foreach (var entry in document.Entries)
        {
            var key = $"{entry.SourceType}\n{entry.SourceId}";
            if (entry.Vector.Count != document.EmbeddingDimension ||
                entry.Vector.Count == 0 ||
                entry.Vector.Any(static value => float.IsNaN(value) || float.IsInfinity(value)) ||
                !sourceHashes.TryGetValue(key, out var sourceHash) ||
                !string.Equals(sourceHash, entry.ContentHash, StringComparison.Ordinal))
            {
                invalid++;
            }
        }

        var duplicateAnomalies = document.Entries
            .GroupBy(entry => string.Join(',', entry.Vector.Select(static value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))), StringComparer.Ordinal)
            .Count(group => group.Select(static entry => entry.EmbeddingInputContentHash).Distinct(StringComparer.Ordinal).Skip(1).Any());
        var metadataValid = document.SchemaVersion == EmbeddingIndexDocument.CurrentSchemaVersion &&
            string.Equals(document.ProductName, productName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(document.EmbeddingModel, embeddingModel, StringComparison.OrdinalIgnoreCase) &&
            document.EmbeddingNormalized &&
            string.Equals(document.DistanceMetric, "cosine", StringComparison.OrdinalIgnoreCase) &&
            document.EmbeddingDimension > 0;
        var valid = metadataValid && invalid == 0 && duplicateAnomalies == 0 && document.Entries.Count == sourceHashes.Count;
        return new EmbeddingIndexValidationResult
        {
            IsValid = valid,
            VectorCount = document.Entries.Count,
            InvalidVectorCount = invalid,
            DuplicateVectorAnomalyCount = duplicateAnomalies,
            Message = valid ? "Embedding index validation passed." : "Embedding index validation failed.",
        };
    }

    private static async Task<IReadOnlyList<EmbeddingSource>> ReadSourcesAsync(
        string productName,
        string productIndexFolder,
        bool sanitizeEmbeddingInput,
        CancellationToken cancellationToken)
    {
        var sources = new List<EmbeddingSource>();
        var cases = await ReadJsonAsync<AiIndexDocument>(Path.Combine(productIndexFolder, AiCaseIndexBuilder.IndexFileName), cancellationToken);
        var manuals = await ReadJsonAsync<AiManualIndexDocument>(Path.Combine(productIndexFolder, AiManualIndexBuilder.IndexFileName), cancellationToken);
        var official = await ReadJsonAsync<AiOfficialDocumentIndexDocument>(Path.Combine(productIndexFolder, AiOfficialDocumentIndexBuilder.IndexFileName), cancellationToken);
        var answerPairs = await ReadJsonAsync<CaseAnswerPairIndexDocument>(Path.Combine(productIndexFolder, CaseAnswerPairIndexDocument.FileName), cancellationToken);
        sources.AddRange(cases?.Notes.Select(note => CreateSource(
            note.Id, "PastCaseNote", productName, note.Title, note.Text, note.SupportNumber ?? note.Id, sanitizeEmbeddingInput)) ?? []);
        sources.AddRange(manuals?.Manuals.Select(manual => CreateSource(
            manual.Id, "Manual", productName, manual.Title, manual.Text,
            manual.DocumentId ?? manual.ArchivePath ?? manual.FilePath ?? manual.Id,
            sanitizeEmbeddingInput)) ?? []);
        sources.AddRange(official?.Documents.Select(document => CreateSource(
            document.Id, "OfficialDoc", productName, document.Title, document.Text, document.Url ?? document.Id, sanitizeEmbeddingInput)) ?? []);
        sources.AddRange(answerPairs?.Pairs.Select(pair => CreateSource(
            pair.Id,
            "ExactPastAnswer",
            string.IsNullOrWhiteSpace(pair.ProductName) ? productName : pair.ProductName,
            pair.QuestionText,
            pair.CustomerReplyText,
            pair.SupportNumber ?? pair.Id,
            sanitizeEmbeddingInput)) ?? []);
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
        string text,
        string embeddingGroupLocator,
        bool sanitizeEmbeddingInput)
    {
        var sourceText = $"{title}\n{text}";
        var embeddingText = sanitizeEmbeddingInput
            ? TechnicalQueryExtractor.Separate(sourceText, context: null).TechnicalText
            : sourceText;
        if (string.IsNullOrWhiteSpace(embeddingText))
        {
            embeddingText = title;
        }
        embeddingText = LimitEmbeddingInput(embeddingText);
        return new EmbeddingSource(
            sourceId,
            sourceType,
            productName,
            embeddingText,
            Hash(sourceText),
            Hash(embeddingText),
            $"{sourceType}:{sourceId}",
            Hash($"{sourceType}\n{embeddingGroupLocator}"),
            sanitizeEmbeddingInput);
    }

    private static string LimitEmbeddingInput(string value) => value.Length <= MaxEmbeddingInputCharacters
        ? value
        : $"{value[..MaxEmbeddingInputCharacters]}...";

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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
        var temporaryPath = $"{indexPath}.{Guid.NewGuid():N}.staging";
        var backupPath = $"{indexPath}.backup";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
            }

            if (File.Exists(indexPath))
            {
                File.Copy(indexPath, backupPath, overwrite: true);
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

    private static IReadOnlyList<float> NormalizeVector(IReadOnlyList<float> vector)
    {
        var magnitude = Math.Sqrt(vector.Sum(value => (double)value * value));
        return magnitude <= 0
            ? []
            : vector.Select(value => (float)(value / magnitude)).ToList();
    }

    private sealed record EmbeddingSource(
        string SourceId,
        string SourceType,
        string ProductName,
        string EmbeddingText,
        string ContentHash,
        string EmbeddingInputContentHash,
        string DocumentLocator,
        string EmbeddingGroupKey,
        bool EmbeddingInputSanitized);

    private sealed record EmbeddingGroup(
        string Key,
        string EmbeddingText,
        bool EmbeddingInputSanitized);
}
