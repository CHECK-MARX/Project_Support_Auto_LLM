using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Indexing;

public static partial class PastQuestionNormalizer
{
    public static string Normalize(string? value, string? companyName = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC)
            .ToLower(CultureInfo.InvariantCulture)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (!string.IsNullOrWhiteSpace(companyName))
        {
            normalized = normalized.Replace(
                companyName.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture),
                " ",
                StringComparison.Ordinal);
        }

        normalized = EmailRegex().Replace(normalized, " ");
        normalized = SupportNumberRegex().Replace(normalized, " ");
        normalized = DateRegex().Replace(normalized, " ");
        normalized = CompanySalutationRegex().Replace(normalized, " ");
        normalized = ContactSalutationRegex().Replace(normalized, " ");

        var retainedLines = normalized.Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Where(static line => !IsGreetingOrSignatureLine(line));
        var joined = string.Join(' ', retainedLines);
        var builder = new StringBuilder(joined.Length);
        foreach (var character in joined)
        {
            if (char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) ||
                character is '.' or ':' or '_' or '-' or '/' or '\\' or '+' or '#' or '&')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append(' ');
            }
        }

        return WhitespaceRegex().Replace(builder.ToString(), " ").Trim();
    }

    public static string Hash(string normalizedQuestion)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedQuestion)))
            .ToLowerInvariant();
    }

    public static double Similarity(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1;
        }

        var leftBigrams = Bigrams(left);
        var rightBigrams = Bigrams(right);
        if (leftBigrams.Count == 0 || rightBigrams.Count == 0)
        {
            return 0;
        }

        var intersection = leftBigrams.Intersect(rightBigrams, StringComparer.Ordinal).Count();
        return Math.Clamp((2d * intersection) / (leftBigrams.Count + rightBigrams.Count), 0, 1);
    }

    private static HashSet<string> Bigrams(string value)
    {
        var compact = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compact.Length < 2)
        {
            return string.IsNullOrWhiteSpace(compact) ? [] : [compact];
        }

        return Enumerable.Range(0, compact.Length - 1)
            .Select(index => compact.Substring(index, 2))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsGreetingOrSignatureLine(string line)
    {
        return line.Contains("いつもお世話になって", StringComparison.Ordinal)
            || line.Contains("お世話になっております", StringComparison.Ordinal)
            || line.Contains("よろしくお願いいたします", StringComparison.Ordinal)
            || line.Contains("よろしくお願い申し上げます", StringComparison.Ordinal)
            || line is "以上" or "以上です"
            || line.StartsWith("tel", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("fax", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("〒", StringComparison.Ordinal)
            || line.All(character => character is '-' or '_' or '=' or ' ');
    }

    [GeneratedRegex(@"[\p{L}\p{N}・()（）\s]{0,50}(?:株式会社|有限会社|合同会社)[\p{L}\p{N}・()（）\s]{0,40}(?:様|御中)", RegexOptions.CultureInvariant)]
    private static partial Regex CompanySalutationRegex();

    [GeneratedRegex(@"\b[\w.!#$%&'*+/=?^`{|}~-]+@[\w.-]+\.[a-z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?:サポート番号|案件番号|case\s*id)\s*[:：#]?\s*[a-z0-9_-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupportNumberRegex();

    [GeneratedRegex(@"\b20\d{2}(?:[./-]\d{1,2}[./-]\d{1,2}|年\d{1,2}月\d{1,2}日)(?:\s+\d{1,2}:\d{2}(?::\d{2})?)?", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"(?:ご担当者|担当者)\s*様", RegexOptions.CultureInvariant)]
    private static partial Regex ContactSalutationRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
