using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SupportCaseManager.App.AiHandoff;

public sealed class AiAssistantExecutableResolver : IAiAssistantExecutableResolver
{
    public const string EnvironmentVariableName = "SUPPORT_CASE_AI_ASSISTANT_EXE";
    public const string ExecutableName = "SupportCaseManager.AiAssistant.App.exe";

    private readonly Func<string, string?> getEnvironmentVariable;
    private readonly Func<string, bool> fileExists;
    private readonly string appBaseDirectory;
    private readonly string currentDirectory;

    public AiAssistantExecutableResolver(
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? fileExists = null,
        string? appBaseDirectory = null,
        string? currentDirectory = null)
    {
        this.getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        this.fileExists = fileExists ?? File.Exists;
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

        foreach (var candidate in GetCandidatePaths())
        {
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"AI回答支援アプリの実行ファイルが見つかりません。環境変数 {EnvironmentVariableName} を設定してください。");
    }

    public IReadOnlyList<string> GetCandidatePaths()
    {
        var candidates = new List<string>
        {
            ToFullPath(Path.Combine(appBaseDirectory, ExecutableName)),
            ToFullPath(Path.Combine(
                appBaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "SupportCaseManager.AiAssistant.App",
                "bin",
                "Debug",
                "net10.0-windows",
                ExecutableName)),
            ToFullPath(Path.Combine(
                appBaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "SupportCaseManager.AiAssistant.App",
                "bin",
                "Release",
                "net10.0-windows",
                ExecutableName)),
            ToFullPath(Path.Combine(
                currentDirectory,
                "src",
                "SupportCaseManager.AiAssistant.App",
                "bin",
                "Debug",
                "net10.0-windows",
                ExecutableName)),
            ToFullPath(Path.Combine(
                currentDirectory,
                "src",
                "SupportCaseManager.AiAssistant.App",
                "bin",
                "Release",
                "net10.0-windows",
                ExecutableName)),
        };

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
