using System;
using System.Diagnostics;

namespace SupportCaseManager.App.AiHandoff;

public sealed class ProcessStarter : IProcessStarter
{
    public IStartedProcess Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("AI回答支援アプリの起動に失敗しました。");
        return new StartedProcess(process);
    }

    private sealed class StartedProcess(Process process) : IStartedProcess
    {
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public void Dispose() => process.Dispose();
    }
}
