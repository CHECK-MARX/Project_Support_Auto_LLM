using System.Diagnostics;

namespace SupportCaseManager.App.AiHandoff;

public interface IProcessStarter
{
    IStartedProcess Start(ProcessStartInfo startInfo);
}

public interface IStartedProcess : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }
}
