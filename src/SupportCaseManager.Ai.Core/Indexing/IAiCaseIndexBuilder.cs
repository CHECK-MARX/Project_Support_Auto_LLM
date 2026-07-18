namespace SupportCaseManager.Ai.Core.Indexing;

public interface IAiCaseIndexBuilder
{
    Task<AiCaseIndexBuildResult> BuildAsync(
        string sourceFolder,
        string aiIndexFolder,
        CancellationToken cancellationToken = default);

    Task<AiCaseIndexBuildResult> BuildIncrementalAsync(
        string sourceFolder,
        string aiIndexFolder,
        bool forceRebuild = false,
        CancellationToken cancellationToken = default)
    {
        return BuildAsync(sourceFolder, aiIndexFolder, cancellationToken);
    }
}
