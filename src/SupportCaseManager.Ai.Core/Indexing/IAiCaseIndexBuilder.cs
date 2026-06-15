namespace SupportCaseManager.Ai.Core.Indexing;

public interface IAiCaseIndexBuilder
{
    Task<AiCaseIndexBuildResult> BuildAsync(
        string sourceFolder,
        string aiIndexFolder,
        CancellationToken cancellationToken = default);
}
