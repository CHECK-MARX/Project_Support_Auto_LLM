using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SupportCaseManager.App.AiHandoff;

public sealed class AiAssistantProcessLauncher : IAiAssistantProcessLauncher
{
    private readonly IAiAssistantExecutableResolver executableResolver;
    private readonly IProcessStarter processStarter;

    public AiAssistantProcessLauncher(
        IAiAssistantExecutableResolver? executableResolver = null,
        IProcessStarter? processStarter = null)
    {
        this.executableResolver = executableResolver ?? new AiAssistantExecutableResolver();
        this.processStarter = processStarter ?? new ProcessStarter();
    }

    public async Task LaunchAsync(string contextFilePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(contextFilePath))
        {
            throw new ArgumentException("コンテキストファイルパスが指定されていません。", nameof(contextFilePath));
        }

        if (!File.Exists(contextFilePath))
        {
            throw new FileNotFoundException("AI回答支援アプリへ渡すコンテキストファイルが見つかりません。", contextFilePath);
        }

        var executablePath = executableResolver.Resolve();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
        };
        startInfo.ArgumentList.Add("--context-file");
        startInfo.ArgumentList.Add(contextFilePath);

        using var process = processStarter.Start(startInfo);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        if (process.HasExited)
        {
            throw new InvalidOperationException(
                $"AI回答支援アプリは起動直後に終了しました。終了コード: {process.ExitCode}。診断ログまたはWindowsのイベントログを確認してください。");
        }
    }
}
