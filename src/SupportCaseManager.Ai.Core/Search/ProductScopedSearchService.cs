using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Search;

public sealed class ProductScopedSearchService : IProductScopedSearchService
{
    private readonly IAiCaseKeywordSearcher caseKeywordSearcher;
    private readonly IAiManualKeywordSearcher manualKeywordSearcher;
    private readonly IAiOfficialDocumentKeywordSearcher officialDocumentKeywordSearcher;

    public ProductScopedSearchService(
        IAiCaseKeywordSearcher caseKeywordSearcher,
        IAiManualKeywordSearcher manualKeywordSearcher,
        IAiOfficialDocumentKeywordSearcher? officialDocumentKeywordSearcher = null)
    {
        this.caseKeywordSearcher = caseKeywordSearcher ?? throw new ArgumentNullException(nameof(caseKeywordSearcher));
        this.manualKeywordSearcher = manualKeywordSearcher ?? throw new ArgumentNullException(nameof(manualKeywordSearcher));
        this.officialDocumentKeywordSearcher = officialDocumentKeywordSearcher ?? new AiOfficialDocumentKeywordSearcher();
    }

    public async Task<IReadOnlyList<SearchSource>> SearchPastCasesAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        var results = await caseKeywordSearcher.SearchAsync(productIndexFolder, query, maxResults, cancellationToken);
        return AttachProductName(results, product.ProductName);
    }

    public async Task<IReadOnlyList<SearchSource>> SearchManualsAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, product.ProductName);
        var results = await manualKeywordSearcher.SearchAsync(productIndexFolder, query, maxResults, cancellationToken);
        return AttachProductName(results, product.ProductName);
    }

    public async Task<IReadOnlyList<SearchSource>> SearchOfficialDocumentsAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        InquiryFocus inquiryFocus,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var results = await officialDocumentKeywordSearcher.SearchAsync(
            product.ProductName,
            aiIndexFolder,
            inquiryFocus,
            maxResults,
            cancellationToken);
        return AttachProductName(results, product.ProductName);
    }

    public async Task<IReadOnlyList<SearchSource>> SearchAllAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        InquiryFocus inquiryFocus,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(inquiryFocus);
        var query = inquiryFocus.FocusText;
        var perTypeLimit = Math.Max(maxResults, 1);
        var official = await SearchOfficialDocumentsAsync(product, aiIndexFolder, inquiryFocus, perTypeLimit, cancellationToken);
        var manuals = await SearchManualsAsync(product, aiIndexFolder, query, perTypeLimit, cancellationToken);
        var pastCases = await SearchPastCasesAsync(product, aiIndexFolder, query, perTypeLimit, cancellationToken);

        return official
            .Concat(manuals)
            .Concat(pastCases)
            .GroupBy(static source => source.SourceId, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(source => source.Score ?? 0).First())
            .OrderBy(source => SourcePriority(source.SourceType, inquiryFocus.IsFreshnessSensitive))
            .ThenByDescending(static source => source.Score ?? 0)
            .ThenBy(static source => source.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }

    private static IReadOnlyList<SearchSource> AttachProductName(
        IReadOnlyList<SearchSource> sources,
        string productName)
    {
        return sources
            .Select(source => source with { ProductName = productName })
            .ToList();
    }

    private static int SourcePriority(string? sourceType, bool freshnessSensitive)
    {
        if (freshnessSensitive)
        {
            return sourceType switch
            {
                "OfficialDoc" => 0,
                "Manual" => 1,
                "PastCaseNote" => 2,
                _ => 3,
            };
        }

        return sourceType switch
        {
            "Manual" => 0,
            "OfficialDoc" => 1,
            "PastCaseNote" => 2,
            _ => 3,
        };
    }
}
