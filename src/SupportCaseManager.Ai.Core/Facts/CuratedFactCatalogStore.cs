using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Facts;

public static class CuratedFactCatalogStore
{
    public const string FileName = "curated-facts.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string GetFilePath(string aiIndexFolder, string productName)
    {
        return Path.Combine(
            ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName),
            FileName);
    }

    public static async Task SaveAsync(
        string aiIndexFolder,
        CuratedFactCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var filePath = GetFilePath(aiIndexFolder, catalog.ProductName);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, cancellationToken);
    }

    public static CuratedFactCatalog? Load(string aiIndexFolder, string productName)
    {
        if (string.IsNullOrWhiteSpace(aiIndexFolder) || string.IsNullOrWhiteSpace(productName))
        {
            return null;
        }

        var filePath = GetFilePath(aiIndexFolder, productName);
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            return JsonSerializer.Deserialize<CuratedFactCatalog>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static CuratedFactCatalog? LoadOrDefault(string aiIndexFolder, string productName, string inquiryText)
    {
        return Load(aiIndexFolder, productName)
            ?? BuiltInCuratedFactCatalogs.TryGet(productName, inquiryText);
    }

    public static CuratedFactCatalog? GetBuiltInDefault(string productName, string inquiryText)
    {
        return BuiltInCuratedFactCatalogs.TryGet(productName, inquiryText);
    }

    public static IReadOnlyList<ResolvedFact> ToResolvedFacts(CuratedFactCatalog? catalog)
    {
        if (catalog is null)
        {
            return [];
        }

        var facts = new List<ResolvedFact>();
        AddCuratedFact(facts, FactKeys.LatestSastVersion, catalog.LatestSastVersion, catalog.SourceUrls);
        AddCuratedFact(facts, FactKeys.LatestEnginePackVersion, catalog.LatestEnginePackVersion, catalog.SourceUrls);
        AddCuratedFact(facts, FactKeys.LatestHotfixVersion, catalog.LatestHotfixVersion, catalog.SourceUrls);
        return facts;
    }

    private static void AddCuratedFact(
        List<ResolvedFact> facts,
        string key,
        string value,
        IReadOnlyList<string> sourceUrls)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        facts.Add(new ResolvedFact
        {
            Key = key,
            Value = value.Trim(),
            Status = FactStatuses.Confirmed,
            Confidence = FactConfidences.High,
            SourceType = "Curated",
            SourceUrls = sourceUrls,
            Explanation = "CuratedFactCatalogの正本値です。",
        });
    }

    private static class BuiltInCuratedFactCatalogs
    {
        public static CuratedFactCatalog? TryGet(string productName, string inquiryText)
        {
            return IsCheckmarxSast(productName, inquiryText)
                ? Checkmarx()
                : null;
        }

        private static CuratedFactCatalog Checkmarx()
        {
            return new CuratedFactCatalog
            {
                ProductName = "Checkmarx",
                LatestSastVersion = "9.7.0",
                LatestEnginePackVersion = "9.7.6",
                LatestHotfixVersion = "HF10",
                SourceUrls =
                [
                    "https://docs.checkmarx.com/en/34965-321884-release-notes-for-9-7-0.html",
                    "https://docs.checkmarx.com/en/34965-591177-engine-pack-version-9-7-6.html",
                    "https://docs.checkmarx.com/en/34965-337700-hotfixes-9-7-0.html",
                ],
                UpdatedAt = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero),
            };
        }

        private static bool IsCheckmarxSast(string productName, string inquiryText)
        {
            var combined = $"{productName} {inquiryText}";
            return combined.Contains("Checkmarx", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("CxSAST", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("ＣｘＳＡＳＴ", StringComparison.OrdinalIgnoreCase);
        }
    }
}
