using System;
using System.Diagnostics;

namespace SupportCaseManager.App.AiHandoff;

public sealed class ProcessStarter : IProcessStarter
{
    public void Start(ProcessStartInfo startInfo)
    {
        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("AI回答支援アプリの起動に失敗しました。");
        }
    }
}
