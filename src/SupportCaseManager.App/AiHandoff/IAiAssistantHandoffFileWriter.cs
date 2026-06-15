using System.Threading;
using System.Threading.Tasks;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.App.AiHandoff;

public interface IAiAssistantHandoffFileWriter
{
    Task<string> WriteAsync(
        AiAssistantLaunchContext context,
        CancellationToken cancellationToken = default);
}
