using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SupportCaseManager.Core.Config;

public class ProductDefinition
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = new();

    public string BaseFolder { get; set; } = string.Empty;

    public string ClosedFolder { get; set; } = string.Empty;

    public string ProductPromptFilePath { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public int SortOrder { get; set; }
}

public static class ProductDefinitionDefaults
{
    public const string CommonPromptFilePath = "prompts/common-support-rules.txt";

    public static readonly Guid HelixQacId = Guid.Parse("179dd5ca-7bf0-4f5b-935c-7753bb898a01");
    public static readonly Guid CheckmarxId = Guid.Parse("895b7477-9aa9-4b43-a5f4-e389fc563202");
    public static readonly Guid KlocworkId = Guid.Parse("a3e375a5-5c67-412e-9118-e6065149c303");

    public static Guid GetInitialId(string? displayName)
    {
        if (Matches(displayName, "HelixQAC", "Helix QAC", "QAC"))
        {
            return HelixQacId;
        }

        if (Matches(displayName, "Checkmarx", "CxSAST", "SAST"))
        {
            return CheckmarxId;
        }

        if (Matches(displayName, "Klocwork", "Klcwork", "KW"))
        {
            return KlocworkId;
        }

        return Guid.NewGuid();
    }

    public static List<string> GetInitialAliases(string? displayName)
    {
        if (Matches(displayName, "HelixQAC", "Helix QAC", "QAC"))
        {
            return ["Helix QAC", "QAC", "Perforce QAC"];
        }

        if (Matches(displayName, "Checkmarx", "CxSAST", "SAST"))
        {
            return ["CxSAST", "SAST", "Checkmarx SAST"];
        }

        if (Matches(displayName, "Klocwork", "Klcwork", "KW"))
        {
            return string.Equals(displayName?.Trim(), "Klcwork", StringComparison.OrdinalIgnoreCase)
                ? ["Klocwork", "KW"]
                : ["KW"];
        }

        return [];
    }

    public static string GetInitialPromptPath(string? displayName)
    {
        if (Matches(displayName, "HelixQAC", "Helix QAC", "QAC"))
        {
            return "prompts/products/qac.txt";
        }

        if (Matches(displayName, "Checkmarx", "CxSAST", "SAST"))
        {
            return "prompts/products/checkmarx.txt";
        }

        if (Matches(displayName, "Klocwork", "Klcwork", "KW"))
        {
            return "prompts/products/klocwork.txt";
        }

        return string.Empty;
    }

    private static bool Matches(string? value, params string[] candidates)
    {
        return candidates.Any(candidate => string.Equals(value?.Trim(), candidate, StringComparison.OrdinalIgnoreCase));
    }
}

public static class ProductDefinitionValidator
{
    public static IReadOnlyList<string> ValidateAll(IEnumerable<ProductDefinition> products)
    {
        var materialized = products?.ToList() ?? [];
        var errors = new List<string>();

        for (var index = 0; index < materialized.Count; index++)
        {
            var product = materialized[index];
            var label = string.IsNullOrWhiteSpace(product.DisplayName)
                ? $"{index + 1}行目"
                : product.DisplayName.Trim();

            if (string.IsNullOrWhiteSpace(product.DisplayName))
            {
                errors.Add($"{label}: 製品表示名は必須です。");
            }

            if (string.IsNullOrWhiteSpace(product.BaseFolder))
            {
                errors.Add($"{label}: ベースフォルダは必須です。");
            }

            if (string.IsNullOrWhiteSpace(product.ClosedFolder))
            {
                errors.Add($"{label}: クローズフォルダは必須です。");
            }

            if (!string.IsNullOrWhiteSpace(product.BaseFolder) && !IsValidPath(product.BaseFolder))
            {
                errors.Add($"{label}: ベースフォルダのパス形式が不正です。");
            }

            if (!string.IsNullOrWhiteSpace(product.ClosedFolder) && !IsValidPath(product.ClosedFolder))
            {
                errors.Add($"{label}: クローズフォルダのパス形式が不正です。");
            }

            if (!string.IsNullOrWhiteSpace(product.ProductPromptFilePath) && !IsValidPath(product.ProductPromptFilePath))
            {
                errors.Add($"{label}: Codex指示ファイルのパス形式が不正です。");
            }
        }

        errors.AddRange(materialized
            .Where(product => !string.IsNullOrWhiteSpace(product.DisplayName))
            .GroupBy(product => product.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"同じ製品表示名を重複して登録できません: {group.Key}"));

        return errors;
    }

    public static IReadOnlyList<string> GetWarnings(IEnumerable<ProductDefinition> products)
    {
        return (products ?? [])
            .Where(product => string.IsNullOrWhiteSpace(product.ProductPromptFilePath))
            .Select(product => $"{product.DisplayName}: Codex指示ファイルが未設定です。共通指示のみ使用します。")
            .ToList();
    }

    public static IReadOnlyList<ProductDefinition> EnabledInDisplayOrder(IEnumerable<ProductDefinition> products)
    {
        return (products ?? [])
            .Where(product => product.IsEnabled)
            .OrderBy(product => product.SortOrder)
            .ThenBy(product => product.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsValidPath(string path)
    {
        try
        {
            _ = Path.GetFullPath(path);
            return path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch
        {
            return false;
        }
    }
}
