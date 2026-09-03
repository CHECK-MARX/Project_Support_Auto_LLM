using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

/// <summary>
/// Stable, machine-independent identity for an indexed source. It is intentionally
/// separate from legacy source identifiers, which can include absolute paths.
/// </summary>
public sealed record class LogicalSourceLocator
{
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    public static bool TryCreateManual(string corpusRoot, string sourcePath, out LogicalSourceLocator locator)
    {
        locator = new LogicalSourceLocator();
        if (string.IsNullOrWhiteSpace(corpusRoot) || string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        var root = Path.GetFullPath(corpusRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(sourcePath);
        var relative = Path.GetRelativePath(root, candidate).Replace('\\', '/');
        if (!IsSafeRelativePath(relative))
        {
            return false;
        }

        locator = new LogicalSourceLocator
        {
            Value = $"manual://{BuildRootToken(root)}/{Uri.EscapeDataString(relative)}",
        };
        return true;
    }

    public static bool TryCreateArchiveEntry(
        string corpusRoot,
        string archivePath,
        string entryPath,
        out LogicalSourceLocator locator)
    {
        locator = new LogicalSourceLocator();
        if (!TryCreateManual(corpusRoot, archivePath, out var archiveLocator) || !IsSafeRelativePath(entryPath.Replace('\\', '/')))
        {
            return false;
        }

        locator = new LogicalSourceLocator
        {
            Value = $"{archiveLocator.Value}!/{Uri.EscapeDataString(entryPath.Replace('\\', '/'))}",
        };
        return true;
    }

    public static LogicalSourceLocator CreateOfficial(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("An absolute HTTP(S) URL is required.", nameof(url));
        }

        return new LogicalSourceLocator { Value = $"official://{uri.Host.ToLowerInvariant()}{uri.AbsolutePath}" };
    }

    public static bool TryResolveManual(string corpusRoot, LogicalSourceLocator locator, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(corpusRoot) || string.IsNullOrWhiteSpace(locator?.Value))
        {
            return false;
        }

        var root = Path.GetFullPath(corpusRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = $"manual://{BuildRootToken(root)}/";
        if (!locator.Value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encodedRelative = locator.Value[prefix.Length..];
        var relative = Uri.UnescapeDataString(encodedRelative);
        if (!IsSafeRelativePath(relative))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        resolvedPath = candidate;
        return true;
    }

    public static string CreateLogicalSourceId(string productName, string sourceType, LogicalSourceLocator locator)
    {
        var raw = string.Join("\n", productName.Trim(), sourceType.Trim(), locator.Value);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"lsrc:{Convert.ToHexString(hash)[..24].ToLowerInvariant()}";
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        return value.Split('/', StringSplitOptions.None)
            .All(static segment => !string.IsNullOrWhiteSpace(segment) && segment is not "." and not "..");
    }

    private static string BuildRootToken(string root)
    {
        var corpusName = Path.GetFileName(root);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(corpusName.Normalize(NormalizationForm.FormC)));
        return $"root-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }
}

public sealed record class ParsedSourceAddress
{
    [JsonPropertyName("logicalSourceId")]
    public string LogicalSourceId { get; init; } = string.Empty;

    [JsonPropertyName("artifactKey")]
    public string ArtifactKey { get; init; } = string.Empty;

    [JsonPropertyName("pageNumber")]
    public int? PageNumber { get; init; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = string.Empty;
}

public sealed record class ChunkLocator
{
    [JsonPropertyName("logicalSourceId")]
    public string LogicalSourceId { get; init; } = string.Empty;

    [JsonPropertyName("chunkOrdinal")]
    public int ChunkOrdinal { get; init; }

    [JsonPropertyName("pageNumber")]
    public int? PageNumber { get; init; }

    [JsonPropertyName("sectionTitle")]
    public string? SectionTitle { get; init; }

    // Offsets are UTF-16 code unit positions in the named parsed text scope.
    [JsonPropertyName("offsetBasis")]
    public string OffsetBasis { get; init; } = string.Empty;

    [JsonPropertyName("startOffset")]
    public int StartOffset { get; init; }

    [JsonPropertyName("length")]
    public int Length { get; init; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = string.Empty;
}

public sealed record class SourceRegistryDocument
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "source-provenance-registry.json";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("sources")]
    public IReadOnlyList<SourceRegistryEntry> Sources { get; init; } = [];
}

public sealed record class SourceRegistryEntry
{
    [JsonPropertyName("logicalSourceId")]
    public string LogicalSourceId { get; init; } = string.Empty;

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = string.Empty;

    [JsonPropertyName("sourceType")]
    public string SourceType { get; init; } = string.Empty;

    [JsonPropertyName("logicalLocator")]
    public LogicalSourceLocator LogicalLocator { get; init; } = new();

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = string.Empty;

    [JsonPropertyName("parsedArtifactKey")]
    public string ParsedArtifactKey { get; init; } = string.Empty;

    [JsonPropertyName("parserVersion")]
    public string ParserVersion { get; init; } = string.Empty;
}

public sealed record class ParsedSourceArtifactDocument
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "parsed-source-artifacts.json";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("sources")]
    public IReadOnlyList<ParsedSourceArtifact> Sources { get; init; } = [];
}

public sealed record class ParsedSourceArtifact
{
    [JsonPropertyName("logicalSourceId")]
    public string LogicalSourceId { get; init; } = string.Empty;

    [JsonPropertyName("artifactKey")]
    public string ArtifactKey { get; init; } = string.Empty;

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = string.Empty;

    [JsonPropertyName("parserVersion")]
    public string ParserVersion { get; init; } = string.Empty;

    [JsonPropertyName("pages")]
    public IReadOnlyList<ParsedSourcePage> Pages { get; init; } = [];
}

public sealed record class ParsedSourcePage
{
    [JsonPropertyName("pageNumber")]
    public int? PageNumber { get; init; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}
