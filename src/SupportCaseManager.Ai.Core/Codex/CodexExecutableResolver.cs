using System.Diagnostics;

namespace SupportCaseManager.Ai.Core.Codex;

public enum CodexExecutableSource
{
    UserSetting,
    PathEnvironment,
    StandardLocation,
    NotFound,
}

public sealed record CodexExecutableResolution
{
    public string? ExecutablePath { get; init; }
    public CodexExecutableSource Source { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Found => !string.IsNullOrWhiteSpace(ExecutablePath);
}

public interface ICodexExecutableResolver
{
    Task<CodexExecutableResolution> ResolveAsync(string? configuredPath, CancellationToken cancellationToken = default);
    Task<string?> GetVersionAsync(string executablePath, CancellationToken cancellationToken = default);
}

public sealed class CodexExecutableResolver : ICodexExecutableResolver
{
    private readonly Func<string, bool> fileExists;
    private readonly Func<string> localAppDataProvider;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>> whereProvider;
    private readonly Func<string, CancellationToken, Task<string?>> versionProvider;
    private readonly Func<string, IReadOnlyList<string>> desktopRuntimeProvider;

    public CodexExecutableResolver(
        Func<string, bool>? fileExists = null,
        Func<string>? localAppDataProvider = null,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? whereProvider = null,
        Func<string, CancellationToken, Task<string?>>? versionProvider = null,
        Func<string, IReadOnlyList<string>>? desktopRuntimeProvider = null)
    {
        this.fileExists = fileExists ?? File.Exists;
        this.localAppDataProvider = localAppDataProvider
            ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        this.whereProvider = whereProvider ?? FindWithWhereAsync;
        this.versionProvider = versionProvider ?? ReadVersionAsync;
        this.desktopRuntimeProvider = desktopRuntimeProvider ?? EnumerateDesktopRuntimeCandidates;
    }

    public async Task<CodexExecutableResolution> ResolveAsync(
        string? configuredPath,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = NormalizeCandidate(configuredPath);
            if (configured is not null && fileExists(configured))
            {
                return Found(configured, CodexExecutableSource.UserSetting, "設定されたCodex実行ファイルを使用します。");
            }
        }

        foreach (var candidate in GetDesktopRuntimeCandidates())
        {
            var normalized = NormalizeCandidate(candidate);
            if (normalized is not null && fileExists(normalized))
            {
                return Found(normalized, CodexExecutableSource.StandardLocation, "Codex Desktopの最新runtimeからCodex実行ファイルを検出しました。");
            }
        }

        foreach (var candidate in await whereProvider("codex", cancellationToken))
        {
            var normalized = NormalizeCandidate(candidate);
            if (normalized is not null && fileExists(normalized))
            {
                return Found(normalized, CodexExecutableSource.PathEnvironment, "PATHからCodex実行ファイルを検出しました。");
            }
        }

        var standardCandidates = new[]
        {
            Path.Combine(localAppDataProvider(), "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
            Path.Combine(localAppDataProvider(), "Programs", "Codex", "bin", "codex.exe"),
        };
        foreach (var candidate in standardCandidates)
        {
            if (fileExists(candidate))
            {
                return Found(Path.GetFullPath(candidate), CodexExecutableSource.StandardLocation, "標準インストール先からCodexを検出しました。");
            }
        }

        return new CodexExecutableResolution
        {
            Source = CodexExecutableSource.NotFound,
            Message = "Codex実行ファイルが見つかりません。設定画面の「Codex実行ファイル」でcodex.exeを選択してください。",
        };
    }

    private IReadOnlyList<string> GetDesktopRuntimeCandidates()
    {
        try
        {
            return desktopRuntimeProvider(localAppDataProvider())
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    public Task<string?> GetVersionAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Task.FromResult<string?>(null);
        }

        return versionProvider(executablePath, cancellationToken);
    }

    private static CodexExecutableResolution Found(
        string path,
        CodexExecutableSource source,
        string message)
    {
        return new CodexExecutableResolution
        {
            ExecutablePath = path,
            Source = source,
            Message = message,
        };
    }

    private static string? NormalizeCandidate(string candidate)
    {
        try
        {
            var trimmed = candidate.Trim().Trim('"');
            return string.IsNullOrWhiteSpace(trimmed) ? null : Path.GetFullPath(trimmed);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>> FindWithWhereAsync(
        string executableName,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "where.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add(executableName);
            if (!process.Start())
            {
                return [];
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    private static async Task<string?> ReadVersionAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("--version");
            if (!process.Start())
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> EnumerateDesktopRuntimeCandidates(string localAppData)
    {
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return [];
        }

        var runtimeRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        try
        {
            return Directory.EnumerateFiles(runtimeRoot, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
