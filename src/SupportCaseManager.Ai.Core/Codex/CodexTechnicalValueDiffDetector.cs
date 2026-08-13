using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Codex;

public sealed record CodexTechnicalValueDiff(
    IReadOnlyList<string> AddedValues,
    IReadOnlyList<string> RemovedValues)
{
    public bool HasDifferences => AddedValues.Count > 0 || RemovedValues.Count > 0;
}

public interface ICodexTechnicalValueDiffDetector
{
    CodexTechnicalValueDiff Compare(string before, string after, IEnumerable<string>? productNames = null);
}

public sealed partial class CodexTechnicalValueDiffDetector : ICodexTechnicalValueDiffDetector
{
    public CodexTechnicalValueDiff Compare(string before, string after, IEnumerable<string>? productNames = null)
    {
        var beforeValues = Extract(before, productNames);
        var afterValues = Extract(after, productNames);
        return new CodexTechnicalValueDiff(
            afterValues.Except(beforeValues, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            beforeValues.Except(afterValues, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static HashSet<string> Extract(string? text, IEnumerable<string>? productNames)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var source = text ?? string.Empty;
        AddMatches(values, VersionRegex(), source);
        AddMatches(values, HotfixRegex(), source);
        AddMatches(values, ErrorCodeRegex(), source);
        AddMatches(values, UrlRegex(), source);
        AddMatches(values, WindowsPathRegex(), source);
        AddMatches(values, UnixPathRegex(), source);
        AddMatches(values, BacktickRegex(), source, groupName: "value");
        AddMatches(values, NumberWithUnitRegex(), source);
        foreach (var product in productNames ?? [])
        {
            if (!string.IsNullOrWhiteSpace(product)
                && source.Contains(product, StringComparison.OrdinalIgnoreCase))
            {
                values.Add(product.Trim());
            }
        }

        return values;
    }

    private static void AddMatches(HashSet<string> values, Regex regex, string source, string? groupName = null)
    {
        foreach (Match match in regex.Matches(source))
        {
            var value = groupName is null ? match.Value : match.Groups[groupName].Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.Trim().TrimEnd('.', ',', '、', '。'));
            }
        }
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])v?\d+(?:\.\d+){1,4}(?:[-+][A-Za-z0-9._-]+)?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\b(?:HF|EP)[-_ ]?\d+[A-Za-z0-9._-]*\b", RegexOptions.IgnoreCase)]
    private static partial Regex HotfixRegex();

    [GeneratedRegex(@"\b(?:0x[0-9A-F]+|[A-Z]{1,8}[-_]\d{2,})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorCodeRegex();

    [GeneratedRegex(@"https?://[^\s<>\""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\b[A-Za-z]:\\[^\r\n\t<>\""|?*]+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?<![\w])/(?:[A-Za-z0-9._-]+/)+[A-Za-z0-9._-]+", RegexOptions.IgnoreCase)]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(@"`(?<value>[^`\r\n]{2,120})`")]
    private static partial Regex BacktickRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])\d+(?:\.\d+)?\s*(?:MB|GB|KB|ms|秒|分|%|件|回)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumberWithUnitRegex();
}
