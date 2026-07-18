using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Search;

public interface IProductScopedSearchService
{
    Task<IReadOnlyList<SearchSource>> SearchPastCasesAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchSource>> SearchManualsAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchSource>> SearchOfficialDocumentsAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        InquiryFocus inquiryFocus,
        int maxResults = 8,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchSource>> SearchPastAnswersAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SearchSource>>([]);
    }

    Task<IReadOnlyList<SearchSource>> SearchPastAnswersAcrossProductsAsync(
        IReadOnlyList<ProductKnowledgeSettings> products,
        string aiIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SearchSource>>([]);
    }

    Task<IReadOnlyList<SearchSource>> SearchAllAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        InquiryFocus inquiryFocus,
        int maxResults = 8,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchSource>> SearchAllHybridAsync(
        ProductKnowledgeSettings product,
        string aiIndexFolder,
        InquiryFocus inquiryFocus,
        LlmProviderSettings providerSettings,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        return SearchAllAsync(product, aiIndexFolder, inquiryFocus, maxResults, cancellationToken);
    }
}
