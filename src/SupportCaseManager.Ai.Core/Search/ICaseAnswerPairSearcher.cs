using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Search;

public interface ICaseAnswerPairSearcher
{
    Task<IReadOnlyList<SearchSource>> SearchAsync(
        string productIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchSource>> SearchBySupportNumberAsync(
        string productIndexFolder,
        string supportNumber,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SearchSource>>([]);
    }
}
