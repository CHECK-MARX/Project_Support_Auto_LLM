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
}
