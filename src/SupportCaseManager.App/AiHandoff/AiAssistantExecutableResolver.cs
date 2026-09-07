using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SupportCaseManager.App.AiHandoff;

public sealed class AiAssistantExecutableResolver : IAiAssistantExecutableResolver
{
    public const string EnvironmentVariableName = "SUPPORT_CASE_AI_ASSISTANT_EXE";
    public const string ExecutableName = "SupportCaseManager.AiAssistant.App.exe";
    public const string AssemblyName = "SupportCaseManager.AiAssistant.App.dll";

    private readonly Func<string, string?> getEnvironmentVariable;
    private readonly Func<string, bool> fileExists;
    private readonly Func<string, DateTime> getLastWriteTimeUtc;
    private readonly string appBaseDirectory;
    private readonly string currentDirectory;

    public AiAssistantExecutableResolver(
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? fileExists = null,
        string? appBaseDirectory = null,
        string? currentDirectory = null,
        Func<string, DateTime>? getLastWriteTimeUtc = null)
    {
        this.getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        this.fileExists = fileExists ?? File.Exists;
        this.getLastWriteTimeUtc = getLastWriteTimeUtc ?? File.GetLastWriteTimeUtc;
        this.appBaseDirectory = appBaseDirectory ?? AppContext.BaseDirectory;
        this.currentDirectory = currentDirectory ?? Directory.GetCurrentDirectory();
    }

    public string Resolve()
    {
        var environmentPath = NormalizePath(getEnvironmentVariable(EnvironmentVariableName));
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            if (fileExists(environmentPath))
            {
                return ToFullPath(environmentPath);
            }

            throw new FileNotFoundException(
                $"AI回答支援アプリの実行ファイルが見つかりません。環境変数 {EnvironmentVariableName} を確認してください。",
                environmentPath);
        }

        var candidates = GetCandidatePaths()
            .Where(fileExists)
            .OrderByDescending(GetLastWriteTimeUtcSafely)
            .ThenBy(path => GetCandidatePreference(path))
            .ToList();
        foreach (var candidate in candidates)
        {
            return candidate;
        }

        throw new FileNotFoundException(
            $"AI回答支援アプリの実行ファイルが見つかりません。環境変数 {EnvironmentVariableName} を設定してください。");
    }

    public IReadOnlyList<string> GetCandidatePaths()
    {
        var configurations = PreferredBuildConfigurations();
        var candidates = new List<string>
        {
            ToFullPath(Path.Combine(appBaseDirectory, ExecutableName)),
            ToFullPath(Path.Combine(appBaseDirectory, AssemblyName)),
        };

        foreach (var configuration in configurations)
        {
            candidates.Add(ToFullPath(Path.Combine(
                appBaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "SupportCaseManager.AiAssistant.App",
                "bin",
                configuration,
                "net10.0-windows",
                ExecutableName)));
            candidates.Add(ToFullPath(Path.Combine(
                appBaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "SupportCaseManager.AiAssistant.App",
                "bin",
                configuration,
                "net10.0-windows",
                AssemblyName)));
        }

        foreach (var configuration in configurations)
        {
            candidates.Add(ToFullPath(Path.Combine(
                currentDirectory,
                "src",
                "SupportCaseManager.AiAssistant.App",
                "bin",
                configuration,
                "net10.0-windows",
                ExecutableName)));
            candidates.Add(ToFullPath(Path.Combine(
                currentDirectory,
                "src",
                "SupportCaseManager.AiAssistant.App",
                "bin",
                configuration,
                "net10.0-windows",
                AssemblyName)));
        }

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private DateTime GetLastWriteTimeUtcSafely(string path)
    {
        try
        {
            return getLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private int GetCandidatePreference(string path)
    {
        var configurations = PreferredBuildConfigurations();
        for (var index = 0; index < configurations.Count; index++)
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}{configurations[index]}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || path.Contains($"{Path.AltDirectorySeparatorChar}{configurations[index]}{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return configurations.Count;
    }

    private IReadOnlyList<string> PreferredBuildConfigurations()
    {
        var segments = appBaseDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Contains("Debug", StringComparer.OrdinalIgnoreCase))
        {
            return ["Debug", "Release"];
        }

        return ["Release", "Debug"];
    }

    private static string NormalizePath(string? path)
    {
        return path?.Trim().Trim('"') ?? string.Empty;
    }

    private static string ToFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
