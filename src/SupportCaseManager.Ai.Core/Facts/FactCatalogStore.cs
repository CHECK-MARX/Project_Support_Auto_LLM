using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Facts;

public static class FactCatalogStore
{
    public const string VersionCatalogFileName = "version-catalog.json";
    public const string ProductFactsFileName = "product-facts.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static ProductFactCatalog BuildCatalog(
        string productName,
        IReadOnlyList<CandidateFact> candidateFacts,
        DateTimeOffset updatedAt)
    {
        return new ProductFactCatalog
        {
            ProductName = productName,
            CandidateFacts = candidateFacts,
            ResolvedFacts = ResolveCatalogFacts(candidateFacts),
            UpdatedAt = updatedAt,
        };
    }

    public static async Task SaveAsync(
        string productIndexFolder,
        ProductFactCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(productIndexFolder);
        await SaveOneAsync(Path.Combine(productIndexFolder, VersionCatalogFileName), catalog, cancellationToken);
        await SaveOneAsync(Path.Combine(productIndexFolder, ProductFactsFileName), catalog, cancellationToken);
    }

    public static ProductFactCatalog? Load(string aiIndexFolder, string productName)
    {
        if (string.IsNullOrWhiteSpace(aiIndexFolder) || string.IsNullOrWhiteSpace(productName))
        {
            return null;
        }

        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
        foreach (var fileName in new[] { VersionCatalogFileName, ProductFactsFileName })
        {
            var filePath = Path.Combine(productIndexFolder, fileName);
            if (!File.Exists(filePath))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(filePath);
                return JsonSerializer.Deserialize<ProductFactCatalog>(stream, JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static async Task SaveOneAsync(
        string filePath,
        ProductFactCatalog catalog,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, cancellationToken);
    }

    private static IReadOnlyList<ResolvedFact> ResolveCatalogFacts(IReadOnlyList<CandidateFact> candidateFacts)
    {
        return candidateFacts
            .Where(static fact => fact.Key is FactKeys.LatestSastVersion or FactKeys.LatestEnginePackVersion or FactKeys.LatestHotfixVersion)
            .GroupBy(static fact => fact.Key, StringComparer.OrdinalIgnoreCase)
            .Select(ResolveFactGroup)
            .ToList();
    }

    private static ResolvedFact ResolveFactGroup(IGrouping<string, CandidateFact> group)
    {
        var ordered = group
            .OrderByDescending(static fact => ConfidenceRank(fact.Confidence))
            .ThenByDescending(static fact => VersionOrHotfixRank(fact.Value))
            .ToList();
        var best = ordered[0];
        var distinctValues = ordered
            .Select(static fact => fact.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ResolvedFact
        {
            Key = group.Key,
            Value = best.Value,
            Status = distinctValues.Count == 1 ? FactStatuses.Confirmed : FactStatuses.Candidate,
            Confidence = best.Confidence,
            SourceType = best.SourceType,
            SourceUrls = ordered
                .Where(fact => string.Equals(fact.Value, best.Value, StringComparison.OrdinalIgnoreCase))
                .Select(static fact => fact.SourceUrl)
                .Where(static url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Explanation = distinctValues.Count == 1
                ? "OfficialDocから単一候補として解決しました。"
                : "OfficialDocに複数候補があるため、最上位候補として扱います。",
        };
    }

    private static int ConfidenceRank(string confidence)
    {
        return confidence switch
        {
            FactConfidences.High => 3,
            FactConfidences.Medium => 2,
            _ => 1,
        };
    }

    private static int VersionOrHotfixRank(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"HF(?<n>\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups["n"].Value, out var hotfix))
        {
            return hotfix;
        }

        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rank = 0;
        foreach (var part in parts)
        {
            if (int.TryParse(part, out var number))
            {
                rank = (rank * 1000) + number;
            }
        }

        return rank;
    }
}
