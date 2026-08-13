using System;
using System.Collections.Generic;
using System.IO;
using SupportCaseManager.Core.Compatibility;

namespace SupportCaseManager.Core.Config;

public sealed record class SupportPromptLoadResult
{
    public string CommonInstruction { get; init; } = string.Empty;
    public string ProductInstruction { get; init; } = string.Empty;
    public string? CommonResolvedPath { get; init; }
    public string? ProductResolvedPath { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public static class SupportPromptFileLoader
{
    public static SupportPromptLoadResult Load(
        string? productPromptFilePath,
        string? supportToolSettingsFilePath = null,
        string? commonPromptFilePath = null,
        string? applicationBaseDirectory = null)
    {
        var warnings = new List<string>();
        var effectiveCommonPath = string.IsNullOrWhiteSpace(commonPromptFilePath)
            ? ProductDefinitionDefaults.CommonPromptFilePath
            : commonPromptFilePath.Trim();

        var commonResolvedPath = ResolveExistingPath(effectiveCommonPath, supportToolSettingsFilePath, applicationBaseDirectory);
        var commonInstruction = ReadOrWarn(commonResolvedPath, effectiveCommonPath, "共通指示ファイル", warnings);

        string? productResolvedPath = null;
        var productInstruction = string.Empty;
        if (string.IsNullOrWhiteSpace(productPromptFilePath))
        {
            warnings.Add("製品別Codex指示ファイルが未設定です。共通指示のみ使用します。");
        }
        else
        {
            productResolvedPath = ResolveExistingPath(productPromptFilePath, supportToolSettingsFilePath, applicationBaseDirectory);
            productInstruction = ReadOrWarn(productResolvedPath, productPromptFilePath, "製品別Codex指示ファイル", warnings);
        }

        return new SupportPromptLoadResult
        {
            CommonInstruction = commonInstruction,
            ProductInstruction = productInstruction,
            CommonResolvedPath = commonResolvedPath,
            ProductResolvedPath = productResolvedPath,
            Warnings = warnings,
        };
    }

    public static string? ResolveExistingPath(
        string? configuredPath,
        string? supportToolSettingsFilePath = null,
        string? applicationBaseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        try
        {
            if (Path.IsPathRooted(configuredPath))
            {
                return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
            }

            var settingsDirectory = string.IsNullOrWhiteSpace(supportToolSettingsFilePath)
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(supportToolSettingsFilePath));
            var appDirectory = string.IsNullOrWhiteSpace(applicationBaseDirectory)
                ? AppContext.BaseDirectory
                : applicationBaseDirectory;

            foreach (var root in new[] { settingsDirectory, appDirectory })
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var candidate = Path.GetFullPath(Path.Combine(root, configuredPath));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string ReadOrWarn(
        string? resolvedPath,
        string configuredPath,
        string label,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            warnings.Add($"{label}が見つかりません: {configuredPath}");
            return string.Empty;
        }

        try
        {
            return EncodingPolicy.DecodeNoteText(File.ReadAllBytes(resolvedPath)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            warnings.Add($"{label}を読み込めません: {resolvedPath} ({ex.Message})");
            return string.Empty;
        }
    }
}
