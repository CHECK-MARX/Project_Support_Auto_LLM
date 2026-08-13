using System.Diagnostics;
using System.Text;

namespace SupportCaseManager.Ai.Core.Codex;

public sealed record CodexProcessExitedEventArgs(int? ExitCode, bool WasExpected);

public interface ICodexAppServerProcessHost : IAsyncDisposable
{
    event EventHandler<string>? StandardErrorReceived;
    event EventHandler<CodexProcessExitedEventArgs>? Exited;

    bool IsRunning { get; }
    TextReader StandardOutput { get; }
    TextWriter StandardInput { get; }

    Task StartAsync(string executablePath, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class CodexAppServerProcessHost : ICodexAppServerProcessHost
{
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private Process? process;
    private CancellationTokenSource? stderrCancellation;
    private Task? stderrTask;
    private bool stopRequested;
    private bool disposed;

    public event EventHandler<string>? StandardErrorReceived;
    public event EventHandler<CodexProcessExitedEventArgs>? Exited;

    public bool IsRunning => process is { HasExited: false };

    public TextReader StandardOutput => process?.StandardOutput
        ?? throw new InvalidOperationException("Codex App Serverは起動していません。");

    public TextWriter StandardInput => process?.StandardInput
        ?? throw new InvalidOperationException("Codex App Serverは起動していません。");

    public async Task StartAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Codex実行ファイルが見つかりません。", executablePath);
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            DisposeProcess();
            stopRequested = false;
            var startInfo = CreateStartInfo(executablePath);
            var newProcess = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            newProcess.Exited += OnProcessExited;
            if (!newProcess.Start())
            {
                newProcess.Dispose();
                throw new InvalidOperationException("Codex App Serverを起動できませんでした。");
            }

            process = newProcess;
            stderrCancellation = new CancellationTokenSource();
            stderrTask = ReadStandardErrorAsync(newProcess, stderrCancellation.Token);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = process;
            if (current is null)
            {
                return;
            }

            stopRequested = true;
            try
            {
                current.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
            }

            if (!current.HasExited)
            {
                using var gracefulTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                gracefulTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await current.WaitForExitAsync(gracefulTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    current.Kill(entireProcessTree: true);
                    await current.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            stderrCancellation?.Cancel();
            if (stderrTask is not null)
            {
                try
                {
                    await stderrTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            DisposeProcess();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            disposed = true;
            lifecycleGate.Dispose();
        }
    }

    public static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        return startInfo;
    }

    private async Task ReadStandardErrorAsync(Process source, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await source.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            StandardErrorReceived?.Invoke(this, line);
        }
    }

    private void OnProcessExited(object? sender, EventArgs eventArgs)
    {
        if (sender is not Process exitedProcess)
        {
            return;
        }

        int? exitCode = null;
        try
        {
            exitCode = exitedProcess.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        Exited?.Invoke(this, new CodexProcessExitedEventArgs(exitCode, stopRequested));
    }

    private void DisposeProcess()
    {
        stderrCancellation?.Cancel();
        stderrCancellation?.Dispose();
        stderrCancellation = null;
        stderrTask = null;
        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            process.Dispose();
            process = null;
        }
    }
}
