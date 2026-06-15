using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Search;

public interface IAiOfficialDocumentKeywordSearcher
{
    Task<IReadOnlyList<SearchSource>> SearchAsync(
        string productName,
        string indexFolder,
        InquiryFocus inquiryFocus,
        int maxResults,
        CancellationToken cancellationToken = default);
}
