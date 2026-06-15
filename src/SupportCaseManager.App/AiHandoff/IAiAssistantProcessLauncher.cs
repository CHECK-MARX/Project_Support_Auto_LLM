using System.Threading;
using System.Threading.Tasks;

namespace SupportCaseManager.App.AiHandoff;

public interface IAiAssistantProcessLauncher
{
    Task LaunchAsync(string contextFilePath, CancellationToken cancellationToken = default);
}
