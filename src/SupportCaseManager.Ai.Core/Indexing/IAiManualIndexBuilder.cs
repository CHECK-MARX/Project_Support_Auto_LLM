namespace SupportCaseManager.Ai.Core.Indexing;

public interface IAiManualIndexBuilder
{
    Task<AiManualIndexBuildResult> BuildAsync(
        string manualFolder,
        string aiIndexFolder,
        CancellationToken cancellationToken = default);

    Task<AiManualIndexBuildResult> BuildManyAsync(
        IReadOnlyList<string> manualFolders,
        string aiIndexFolder,
        CancellationToken cancellationToken = default);

    Task<AiManualIndexBuildResult> BuildManyIncrementalAsync(
        IReadOnlyList<string> manualFolders,
        string aiIndexFolder,
        bool forceRebuild = false,
        CancellationToken cancellationToken = default)
    {
        return BuildManyAsync(manualFolders, aiIndexFolder, cancellationToken);
    }
}
