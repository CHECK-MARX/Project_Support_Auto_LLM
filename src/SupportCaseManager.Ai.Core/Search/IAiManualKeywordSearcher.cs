using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Search;

public interface IAiManualKeywordSearcher
{
    Task<IReadOnlyList<SearchSource>> SearchAsync(
        string aiIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default);
}
