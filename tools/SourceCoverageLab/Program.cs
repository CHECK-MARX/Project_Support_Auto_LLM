using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: SourceCoverageLab <settings.json> <isolated-index-root> <product> <official-url>...");
    return 2;
}

var settingsPath = Path.GetFullPath(args[0]);
var indexRoot = Path.GetFullPath(args[1]);
var generatedRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "rag-lab", "reports", "generated"));
if (!indexRoot.StartsWith(generatedRoot + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase) || !Directory.Exists(indexRoot))
{
    throw new ArgumentException("Only an existing isolated index under rag-lab/reports/generated is allowed.");
}

var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var settings = JsonSerializer.Deserialize<AiAssistantSettings>(File.ReadAllText(settingsPath), options)
    ?? throw new InvalidDataException("Settings could not be read.");
var product = settings.Products.Single(value =>
    string.Equals(value.ProductName, args[2], StringComparison.OrdinalIgnoreCase));
var urls = args.Skip(3).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
if (urls.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps ||
                    !new[] { "help.perforce.com", "help.klocwork.com", "docs.checkmarx.com" }
                        .Contains(uri.Host, StringComparer.OrdinalIgnoreCase)))
{
    throw new ArgumentException("Only verified vendor HTTPS URLs are allowed.");
}

var productFolder = ProductIndexPathResolver.GetProductIndexFolder(indexRoot, product.ProductName);
Directory.CreateDirectory(productFolder);
var indexPath = Path.Combine(productFolder, AiOfficialDocumentIndexBuilder.IndexFileName);
var prior = File.Exists(indexPath)
    ? JsonSerializer.Deserialize<AiOfficialDocumentIndexDocument>(File.ReadAllText(indexPath), options)
    : null;
var selected = product with { DocumentUrls = urls, CrawlMaxDepth = 0,
    CrawlMaxPages = Math.Max(16, urls.Length + 10) };
var result = await new AiOfficialDocumentIndexBuilder(includeKnownSeeds: false, chunkMaxLength: 1100)
    .BuildAsync(selected, indexRoot);
var built = JsonSerializer.Deserialize<AiOfficialDocumentIndexDocument>(File.ReadAllText(indexPath), options)
    ?? throw new InvalidDataException("Official index was not written.");
var missing = urls.Where(url => !built.Documents.Any(doc =>
    string.Equals(Uri.UnescapeDataString(doc.Url), Uri.UnescapeDataString(url),
        StringComparison.OrdinalIgnoreCase))).ToArray();
if (missing.Length > 0 || result.FetchFailureCount > 0)
{
    throw new InvalidDataException($"Requested official pages were not indexed: {string.Join(", ", missing)}; failures={result.FetchFailureCount}");
}

var merged = built with
{
    Documents = (prior?.Documents ?? []).Where(doc => !urls.Contains(doc.Url, StringComparer.OrdinalIgnoreCase))
        .Concat(built.Documents)
        .GroupBy(doc => doc.Id, StringComparer.Ordinal)
        .Select(group => group.Last()).ToArray(),
    SourceUrls = (prior?.SourceUrls ?? []).Concat(built.SourceUrls)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
    DiscoveredUrls = (prior?.DiscoveredUrls ?? []).Concat(built.DiscoveredUrls)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
    RetrievedUrls = (prior?.RetrievedUrls ?? []).Concat(built.RetrievedUrls)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
};
var temporaryPath = indexPath + ".merge.tmp";
try
{
    await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(merged, options));
    File.Move(temporaryPath, indexPath, overwrite: true);
}
finally
{
    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
}
Console.WriteLine($"{product.ProductName}: prior={prior?.Documents.Count ?? 0}, fetched={built.Documents.Count}, merged={merged.Documents.Count}, urls={urls.Length}");
return 0;
