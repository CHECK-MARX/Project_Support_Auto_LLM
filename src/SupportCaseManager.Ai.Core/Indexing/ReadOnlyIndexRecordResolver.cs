using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Indexing;

public enum IndexRecordResolutionStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    InvalidLookupKey,
    LegacyProvenanceIncomplete,
}

public sealed record class IndexRecordProvenance
{
    public string SourceType { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string DocumentId { get; init; } = string.Empty;
    public string ChunkId { get; init; } = string.Empty;
    public string IndexLookupKey { get; init; } = string.Empty;
    public string? LogicalSourceId { get; init; }
    public LogicalSourceLocator? LogicalSourceLocator { get; init; }
    public ParsedSourceAddress? ParsedSourceAddress { get; init; }
    public ChunkLocator? ChunkLocator { get; init; }
}

public sealed record class IndexRecordResolution
{
    public IndexRecordResolutionStatus Status { get; init; }
    public IndexRecordProvenance? Record { get; init; }
}

/// <summary>Read-only diagnostic lookup. It is deliberately not part of the search path.</summary>
public sealed class ReadOnlyIndexRecordResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IndexRecordResolution> ResolveByIndexLookupKeyAsync(
        string indexFolder,
        string indexLookupKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(indexFolder) || string.IsNullOrWhiteSpace(indexLookupKey) ||
            indexLookupKey.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(indexLookupKey))
        {
            return new IndexRecordResolution { Status = IndexRecordResolutionStatus.InvalidLookupKey };
        }

        var matches = new List<IndexRecordProvenance>();
        var manuals = await ReadAsync<AiManualIndexDocument>(Path.Combine(indexFolder, AiManualIndexBuilder.IndexFileName), cancellationToken);
        matches.AddRange(manuals?.Manuals
            .Where(manual => string.Equals(GetLookupKey("Manual", manual.Id, manual.IndexLookupKey), indexLookupKey, StringComparison.Ordinal))
            .Select(manual => new IndexRecordProvenance
            {
                SourceType = "Manual",
                SourceId = manual.Id,
                DocumentId = manual.DocumentId ?? string.Empty,
                ChunkId = manual.ChunkId ?? manual.Id,
                IndexLookupKey = GetLookupKey("Manual", manual.Id, manual.IndexLookupKey),
                LogicalSourceId = manual.LogicalSourceId,
                LogicalSourceLocator = manual.LogicalSourceLocator,
                ParsedSourceAddress = manual.ParsedSourceAddress,
                ChunkLocator = manual.ChunkLocator,
            }) ?? []);

        var official = await ReadAsync<AiOfficialDocumentIndexDocument>(Path.Combine(indexFolder, AiOfficialDocumentIndexBuilder.IndexFileName), cancellationToken);
        matches.AddRange(official?.Documents
            .Where(document => string.Equals(GetLookupKey("OfficialDoc", document.Id, document.IndexLookupKey), indexLookupKey, StringComparison.Ordinal))
            .Select(document => new IndexRecordProvenance
            {
                SourceType = "OfficialDoc",
                SourceId = document.Id,
                DocumentId = document.Url,
                ChunkId = document.ChunkId ?? document.Id,
                IndexLookupKey = GetLookupKey("OfficialDoc", document.Id, document.IndexLookupKey),
                LogicalSourceId = document.LogicalSourceId,
                LogicalSourceLocator = document.LogicalSourceLocator,
                ParsedSourceAddress = document.ParsedSourceAddress,
                ChunkLocator = document.ChunkLocator,
            }) ?? []);

        if (matches.Count == 0)
        {
            return new IndexRecordResolution { Status = IndexRecordResolutionStatus.NotFound };
        }
        if (matches.Count > 1)
        {
            return new IndexRecordResolution { Status = IndexRecordResolutionStatus.Ambiguous };
        }

        var match = matches[0];
        return new IndexRecordResolution
        {
            Status = string.IsNullOrWhiteSpace(match.LogicalSourceId) || match.ChunkLocator is null
                ? IndexRecordResolutionStatus.LegacyProvenanceIncomplete
                : IndexRecordResolutionStatus.Resolved,
            Record = match,
        };
    }

    public static string CreateIndexLookupKey(string sourceType, string sourceId) =>
        $"{sourceType.Trim().ToLowerInvariant()}:{sourceId.Trim()}";

    private static string GetLookupKey(string sourceType, string sourceId, string? persistedKey) =>
        string.IsNullOrWhiteSpace(persistedKey) ? CreateIndexLookupKey(sourceType, sourceId) : persistedKey;

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }
}
