using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Launch;

public interface IAiAssistantLaunchContextReader
{
    Task<AiAssistantLaunchContext> ReadAsync(
        string contextFilePath,
        CancellationToken cancellationToken = default);
}
