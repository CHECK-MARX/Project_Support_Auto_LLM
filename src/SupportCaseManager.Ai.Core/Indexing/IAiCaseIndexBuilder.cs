namespace SupportCaseManager.Ai.Core.Indexing;

public interface IAiCaseIndexBuilder
{
    Task<AiCaseIndexBuildResult> BuildAsync(
        string sourceFolder,
        string aiIndexFolder,
        CancellationToken cancellationToken = default);

    Task<AiCaseIndexBuildResult> BuildForProductAsync(
        string sourceFolder,
        string aiIndexFolder,
        string productName,
        CancellationToken cancellationToken = default)
    {
        return BuildAsync(sourceFolder, aiIndexFolder, cancellationToken);
    }

    Task<AiCaseIndexBuildResult> BuildIncrementalAsync(
        string sourceFolder,
        string aiIndexFolder,
        bool forceRebuild = false,
        CancellationToken cancellationToken = default)
    {
        return BuildAsync(sourceFolder, aiIndexFolder, cancellationToken);
    }

    Task<AiCaseIndexBuildResult> BuildIncrementalForProductAsync(
        string sourceFolder,
        string aiIndexFolder,
        string productName,
        bool forceRebuild = false,
        CancellationToken cancellationToken = default)
    {
        return BuildIncrementalAsync(sourceFolder, aiIndexFolder, forceRebuild, cancellationToken);
    }
}
