using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Cases;

public interface ICaseContextBuilder
{
    Task<CaseContext> BuildFromCaseFolderAsync(
        string caseFolderPath,
        string? productName = null,
        string? baseFolder = null,
        string? closeFolder = null,
        CancellationToken cancellationToken = default);
}
