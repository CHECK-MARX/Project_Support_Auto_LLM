using System.Text.Json;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Search;

/// <summary>
/// Resolves versioned release-note requests directly to an exact official document.
/// This path intentionally does not calculate or compare generic search scores.
/// </summary>
public sealed class OfficialDocDirectResolver
{
    public const string RetrievalMode = "OfficialDocDirect";
    public const string MandatoryEvidenceKind = "MANDATORY_OFFICIAL_EVIDENCE";

    private const int DirectExcerptMaxLength = 1200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<SearchSource>> ResolveAsync(
        string productName,
        string indexFolder,
        InquiryFocus inquiryFocus,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (!IsEligible(productName, indexFolder, inquiryFocus, maxResults))
        {
            return [];
        }

        var indexPath = Path.Combine(
            ProductIndexPathResolver.GetProductIndexFolder(indexFolder, productName),
            AiOfficialDocumentIndexBuilder.IndexFileName);
        if (!File.Exists(indexPath))
        {
            return [];
        }

        AiOfficialDocumentIndexDocument? index;
        try
        {
            await using var stream = File.OpenRead(indexPath);
            index = await JsonSerializer.DeserializeAsync<AiOfficialDocumentIndexDocument>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }

        if (index?.Documents.Count is null or 0)
        {
            return [];
        }

        var matches = index.Documents
            .Where(document => ProductMatches(productName, document.ProductName))
            .Select(document => CreateMatch(document, inquiryFocus.TargetVersions))
            .Where(static match => match is not null)
            .Select(static match => match!)
            .ToList();
        if (matches.Count == 0)
        {
            return [];
        }

        var resolvedDocument = matches
            .GroupBy(static match => DocumentKey(match.Document), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ResolvedDocument(
                group.Key,
                group.Select(static match => match.Document).ToList(),
                group.Min(static match => match.MatchRank),
                group.Max(static match => match.Document.RetrievedAt)))
            .OrderBy(static document => document.MatchRank)
            .ThenByDescending(static document => document.LatestRetrievedAt)
            .ThenBy(static document => document.Documents[0].Title, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (resolvedDocument is null)
        {
            return [];
        }

        // Once a page is resolved, retain every chunk belonging to that exact URL.
        // Individual chunks do not all need to repeat the version in their body.
        var resolvedPageDocuments = index.Documents
            .Where(document => ProductMatches(productName, document.ProductName))
            .Where(document => string.Equals(
                DocumentKey(document),
                resolvedDocument.Key,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        var resolvedVersions = inquiryFocus.TargetVersions
            .Where(version => resolvedPageDocuments.Any(document => HasVersionMatch(
                string.Join(' ', document.Title, document.SectionTitle, document.Url, document.Text),
                version)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resolvedTitle = resolvedDocument.Documents
            .Select(static document => BuildTitle(document))
            .FirstOrDefault(static title => !string.IsNullOrWhiteSpace(title))
            ?? "Official release notes";

        return resolvedPageDocuments
            .OrderBy(static document => document.ChunkId ?? document.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static document => document.SectionTitle, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxResults, 1, 100))
            .Select(document => ToSearchSource(document, resolvedTitle, resolvedVersions))
            .ToList();
    }

    private static bool IsEligible(
        string productName,
        string indexFolder,
        InquiryFocus inquiryFocus,
        int maxResults)
    {
        return !string.IsNullOrWhiteSpace(productName)
            && !string.IsNullOrWhiteSpace(indexFolder)
            && inquiryFocus is not null
            && maxResults > 0
            && inquiryFocus.TargetVersions.Count > 0
            && inquiryFocus.TechnicalQuery.Intent.Contains("ReleaseNotes", StringComparer.OrdinalIgnoreCase);
    }

    private static DocumentMatch? CreateMatch(
        AiIndexedOfficialDocument document,
        IReadOnlyList<string> targetVersions)
    {
        var title = string.Join(' ', document.Title, document.SectionTitle);
        var url = document.Url ?? string.Empty;
        var text = document.Text ?? string.Empty;
        var searchable = string.Join(' ', title, url, text);
        if (!HasVersionMatch(searchable, targetVersions))
        {
            return null;
        }

        var matchRank = HasVersionMatch(document.Title, targetVersions)
            ? 0
            : HasVersionMatch(document.SectionTitle, targetVersions)
                ? 1
                : HasVersionMatch(url, targetVersions)
                    ? 2
                    : 3;
        return new DocumentMatch(document, matchRank);
    }

    private static bool ProductMatches(string requestedProduct, string indexedProduct)
    {
        if (string.IsNullOrWhiteSpace(indexedProduct))
        {
            return true;
        }

        if (string.Equals(requestedProduct, indexedProduct, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsCheckmarxProduct(requestedProduct) && IsCheckmarxProduct(indexedProduct);
    }

    private static bool IsCheckmarxProduct(string value) =>
        value.Contains("checkmarx", StringComparison.OrdinalIgnoreCase)
        || value.Contains("cxsast", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value.Trim(), "sast", StringComparison.OrdinalIgnoreCase);

    private static string DocumentKey(AiIndexedOfficialDocument document)
    {
        if (!string.IsNullOrWhiteSpace(document.Url))
        {
            return document.Url.Trim();
        }

        return string.Join("\n", document.Title.Trim(), document.SectionTitle.Trim());
    }

    private static bool HasVersionMatch(string text, IReadOnlyList<string> versions) =>
        versions.Any(version => HasVersionMatch(text, version));

    private static bool HasVersionMatch(string text, string version)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var parts = version
            .Trim()
            .TrimStart('v', 'V')
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(part => !part.All(char.IsDigit)))
        {
            return false;
        }

        var versionPattern = string.Join(@"(?:\.|-)", parts.Select(Regex.Escape));
        return Regex.IsMatch(
            text,
            $@"(?<![A-Za-z0-9])v?{versionPattern}(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static SearchSource ToSearchSource(
        AiIndexedOfficialDocument document,
        string resolvedTitle,
        IReadOnlyList<string> resolvedVersions)
    {
        return new SearchSource
        {
            SourceId = document.Id,
            SourceType = "OfficialDoc",
            Title = BuildTitle(document),
            Text = BuildExcerpt(document.Text),
            Url = document.Url,
            RetrievedAt = document.RetrievedAt,
            Score = 1.0,
            ProductName = document.ProductName,
            MatchedTerms = resolvedVersions
                .Concat(["Release Notes", RetrievalMode])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ScoreBreakdown = $"RetrievalMode={RetrievalMode}; ResolvedDocument={resolvedTitle}; ResolvedVersion={string.Join(",", resolvedVersions)}; MandatoryOfficialEvidence=true; GenericScore=false",
            QueryCoverage = "OfficialDocDirect",
            MatchKind = RetrievalMode,
            EvidenceKind = MandatoryEvidenceKind,
            DocumentId = document.Url,
            SectionTitle = document.SectionTitle,
            ContentHash = document.ContentHash,
            DocumentTitle = document.Title,
            ChunkId = document.ChunkId ?? document.Id,
            LogicalFileId = document.LogicalSourceId,
        };
    }

    private static string BuildTitle(AiIndexedOfficialDocument document) =>
        string.IsNullOrWhiteSpace(document.SectionTitle)
            ? document.Title
            : $"{document.Title} - {document.SectionTitle}";

    private static string BuildExcerpt(string text)
    {
        var normalized = string.Join(
            ' ',
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= DirectExcerptMaxLength
            ? normalized
            : normalized[..DirectExcerptMaxLength] + "...";
    }

    private sealed record DocumentMatch(AiIndexedOfficialDocument Document, int MatchRank);

    private sealed record ResolvedDocument(
        string Key,
        IReadOnlyList<AiIndexedOfficialDocument> Documents,
        int MatchRank,
        DateTimeOffset LatestRetrievedAt);
}
