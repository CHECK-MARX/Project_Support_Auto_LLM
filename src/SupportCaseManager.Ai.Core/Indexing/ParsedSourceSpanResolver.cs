using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Indexing;

public enum ParsedSourceSpanResolutionStatus
{
    Matched,
    SourceUnavailable,
    InvalidSpan,
    HashMismatch,
}

public sealed record class ParsedSourceSpanResolution
{
    public ParsedSourceSpanResolutionStatus Status { get; init; }
    public string Text { get; init; } = string.Empty;
}

/// <summary>Reconstructs a persisted parsed-text span without reading a source file.</summary>
public sealed class ParsedSourceSpanResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<ParsedSourceSpanResolution> ResolveAsync(
        string indexFolder,
        ParsedSourceAddress? parsedSourceAddress,
        ChunkLocator? chunkLocator,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(indexFolder) || parsedSourceAddress is null || chunkLocator is null ||
            !string.Equals(parsedSourceAddress.LogicalSourceId, chunkLocator.LogicalSourceId, StringComparison.Ordinal) ||
            chunkLocator.StartOffset < 0 || chunkLocator.Length <= 0)
        {
            return new ParsedSourceSpanResolution { Status = ParsedSourceSpanResolutionStatus.InvalidSpan };
        }

        var path = Path.Combine(indexFolder, ParsedSourceArtifactDocument.FileName);
        if (!File.Exists(path))
        {
            return new ParsedSourceSpanResolution { Status = ParsedSourceSpanResolutionStatus.SourceUnavailable };
        }

        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<ParsedSourceArtifactDocument>(stream, JsonOptions, cancellationToken);
        var artifact = document?.Sources.SingleOrDefault(source =>
            string.Equals(source.LogicalSourceId, parsedSourceAddress.LogicalSourceId, StringComparison.Ordinal) &&
            string.Equals(source.ArtifactKey, parsedSourceAddress.ArtifactKey, StringComparison.Ordinal));
        var page = artifact?.Pages.SingleOrDefault(candidate => candidate.PageNumber == parsedSourceAddress.PageNumber);
        if (page is null || !string.Equals(page.ContentHash, parsedSourceAddress.ContentHash, StringComparison.Ordinal))
        {
            return new ParsedSourceSpanResolution { Status = ParsedSourceSpanResolutionStatus.SourceUnavailable };
        }
        if (chunkLocator.StartOffset > page.Text.Length || chunkLocator.Length > page.Text.Length - chunkLocator.StartOffset)
        {
            return new ParsedSourceSpanResolution { Status = ParsedSourceSpanResolutionStatus.InvalidSpan };
        }

        var text = page.Text.Substring(chunkLocator.StartOffset, chunkLocator.Length);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new ParsedSourceSpanResolution
        {
            Status = string.Equals(hash, chunkLocator.ContentHash, StringComparison.Ordinal)
                ? ParsedSourceSpanResolutionStatus.Matched
                : ParsedSourceSpanResolutionStatus.HashMismatch,
            Text = text,
        };
    }
}
