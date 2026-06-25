using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Inquiries;

public sealed partial class InquiryFocusExtractor : IInquiryFocusExtractor
{
    private static readonly string[] SectionMarkers =
    [
        "[質問]",
        "【質問】",
        "質問:",
        "質問：",
        "お問い合わせ内容",
        "問い合わせ内容",
    ];

    private static readonly string[] StopWords =
    [
        "よろしく",
        "お願い",
        "お世話",
        "確認",
        "質問",
        "件名",
        "次に",
        "標準",
        "できますか",
        "でしょうか",
        "ご教示",
        "いただけますか",
        "サポートチーム",
        "何卒",
        "します",
        "ください",
        "したい",
        "したいです",
    ];

    private static readonly string[] ImportantKnownTerms =
    [
        "ライセンス認証エラー",
        "ライセンスサーバー名",
        "ライセンスサーバー",
        "ライセンス",
        "認証",
        "エラー",
        "サーバー名",
        "サーバー",
        "ポート番号",
        "ポート",
        "ファイアウォール設定",
        "ファイアウォール",
        "設定",
        "起動",
        "失敗",
        "成功",
        "権限",
        "permission",
        "error",
        "license",
        "server",
        "port",
        "firewall",
        "version",
    ];

    private static readonly string[] FreshnessKeywords =
    [
        "最新",
        "最新版",
        "最新バージョン",
        "current version",
        "latest version",
        "バージョン",
        "version",
        "ep",
        "engine pack",
        "hf",
        "hotfix",
        "リリース",
        "release",
        "パッチ",
        "patch",
        "サポート期限",
        "eol",
        "対応バージョン",
    ];

    private static readonly HashSet<string> StopWordSet = new(
        StopWords.Select(NormalizeTerm),
        StringComparer.Ordinal);

    public InquiryFocus Extract(string inquiryText, CaseContext? caseContext = null)
    {
        if (string.IsNullOrWhiteSpace(inquiryText))
        {
            return new InquiryFocus();
        }

        var focusText = ExtractFocusText(inquiryText);
        var normalizedFocus = NormalizeText(focusText);
        var excludedTerms = FindExcludedTerms(normalizedFocus);
        var targetVersions = ExtractTargetVersions(focusText);
        var terms = ExtractImportantTerms(focusText, normalizedFocus, caseContext);
        var freshness = DetectFreshness(normalizedFocus);

        return new InquiryFocus
        {
            FocusText = focusText.Trim(),
            ImportantTerms = targetVersions.Concat(terms).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList(),
            ExcludedTerms = excludedTerms,
            TargetVersions = targetVersions,
            IsFreshnessSensitive = freshness.IsSensitive,
            FreshnessReason = freshness.Reason,
        };
    }

    private static string ExtractFocusText(string inquiryText)
    {
        var normalized = inquiryText.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var marker in SectionMarkers)
        {
            var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return normalized[(index + marker.Length)..].Trim();
            }
        }

        var meaningfulLines = normalized
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Where(static line => !LooksLikeGreetingOrSignature(line))
            .ToList();

        return meaningfulLines.Count == 0
            ? inquiryText
            : string.Join(Environment.NewLine, meaningfulLines);
    }

    private static IReadOnlyList<string> ExtractImportantTerms(
        string focusText,
        string normalizedFocus,
        CaseContext? caseContext)
    {
        var terms = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var known in ImportantKnownTerms)
        {
            var normalizedKnown = NormalizeTerm(known);
            if (!string.IsNullOrWhiteSpace(normalizedKnown) &&
                ContainsKnownTerm(normalizedFocus, normalizedKnown))
            {
                terms[known] = Math.Max(terms.GetValueOrDefault(known), 100 + known.Length);
            }
        }

        foreach (var token in SplitTokens(focusText))
        {
            var normalizedToken = NormalizeTerm(token);
            if (string.IsNullOrWhiteSpace(normalizedToken) || StopWordSet.Contains(normalizedToken))
            {
                continue;
            }

            var score = ScoreToken(token);
            if (score <= 0)
            {
                continue;
            }

            terms[token] = Math.Max(terms.GetValueOrDefault(token), score);

            if (ContainsJapanese(token))
            {
                foreach (var ngram in CreateJapaneseNGrams(token, minLength: 2, maxLength: 6))
                {
                    var normalizedNGram = NormalizeTerm(ngram);
                    if (!StopWordSet.Contains(normalizedNGram))
                    {
                        terms[ngram] = Math.Max(terms.GetValueOrDefault(ngram), Math.Min(score, 40));
                    }
                }
            }
        }

        foreach (var currentCaseTerm in CurrentCaseTerms(caseContext))
        {
            terms.Remove(currentCaseTerm);
        }

        return terms
            .OrderByDescending(static item => item.Value)
            .ThenByDescending(static item => item.Key.Length)
            .ThenBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => item.Key)
            .Take(24)
            .ToList();
    }

    private static IReadOnlyList<string> FindExcludedTerms(string normalizedFocus)
    {
        return StopWords
            .Where(term => ContainsKnownTerm(normalizedFocus, NormalizeTerm(term)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool ContainsKnownTerm(string normalizedFocus, string normalizedTerm)
    {
        if (string.IsNullOrWhiteSpace(normalizedFocus) || string.IsNullOrWhiteSpace(normalizedTerm))
        {
            return false;
        }

        if (string.Equals(normalizedTerm, "ポート", StringComparison.Ordinal))
        {
            return normalizedFocus.Contains("ポート番号", StringComparison.Ordinal) ||
                normalizedFocus.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("ポート", StringComparer.Ordinal);
        }

        if (string.Equals(normalizedTerm, "port", StringComparison.Ordinal))
        {
            return normalizedFocus.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("port", StringComparer.Ordinal);
        }

        return normalizedFocus.Contains(normalizedTerm, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ExtractTargetVersions(string focusText)
    {
        return VersionNumberRegex()
            .Matches(focusText)
            .Select(static match => match.Value.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static (bool IsSensitive, string Reason) DetectFreshness(string normalizedFocus)
    {
        var matched = FreshnessKeywords
            .Where(keyword => normalizedFocus.Contains(NormalizeTerm(keyword), StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return matched.Count == 0
            ? (false, string.Empty)
            : (true, $"{string.Join(" / ", matched)} を含むため");
    }

    private static IEnumerable<string> SplitTokens(string text)
    {
        foreach (var raw in TokenSeparatorRegex().Split(text.Normalize(NormalizationForm.FormKC)))
        {
            var token = raw.Trim();
            if (token.Length >= 2)
            {
                yield return token;
            }
        }
    }

    private static IEnumerable<string> CreateJapaneseNGrams(string token, int minLength, int maxLength)
    {
        if (token.Length > 80)
        {
            yield break;
        }

        for (var length = minLength; length <= maxLength && length <= token.Length; length++)
        {
            for (var start = 0; start <= token.Length - length; start++)
            {
                yield return token.Substring(start, length);
            }
        }
    }

    private static IEnumerable<string> CurrentCaseTerms(CaseContext? context)
    {
        if (context is null)
        {
            yield break;
        }

        foreach (var value in new[] { context.CompanyName, context.CustomerName, context.SupportNumber })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value.Trim();
            }
        }
    }

    private static int ScoreToken(string token)
    {
        if (AsciiProductOrVersionRegex().IsMatch(token))
        {
            return 85;
        }

        if (ContainsJapanese(token))
        {
            if (token.Length >= 4)
            {
                return 70;
            }

            return 45;
        }

        return token.Length >= 4 ? 55 : 20;
    }

    private static bool LooksLikeGreetingOrSignature(string line)
    {
        if (LooksLikeMeaningfulRequestLine(line))
        {
            return false;
        }

        var normalized = NormalizeTerm(line);
        if (normalized.Length <= 2)
        {
            return true;
        }

        return StopWordSet.Any(stopWord => normalized.Contains(stopWord, StringComparison.Ordinal))
            && !ImportantKnownTerms.Any(term => normalized.Contains(NormalizeTerm(term), StringComparison.Ordinal));
    }

    private static bool LooksLikeMeaningfulRequestLine(string line)
    {
        return AsciiProductOrVersionRegex().IsMatch(line) ||
            line.Contains("手順書", StringComparison.Ordinal) ||
            line.Contains("利用方法", StringComparison.Ordinal) ||
            line.Contains("設定手順", StringComparison.Ordinal) ||
            line.Contains("トラブルシューティング", StringComparison.Ordinal) ||
            line.Contains("マニュアル", StringComparison.Ordinal) ||
            line.Contains("ドキュメント", StringComparison.Ordinal);
    }

    private static string NormalizeText(string value)
    {
        return string.Join(" ", SplitTokens(value).Select(NormalizeTerm));
    }

    private static string NormalizeTerm(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (!char.IsWhiteSpace(ch) && !IsSeparator(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static bool ContainsJapanese(string value)
    {
        return value.Any(static ch =>
            (ch >= '\u3040' && ch <= '\u30FF')
            || (ch >= '\u3400' && ch <= '\u9FFF'));
    }

    private static bool IsSeparator(char ch)
    {
        return char.GetUnicodeCategory(ch) switch
        {
            UnicodeCategory.ConnectorPunctuation
                or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation
                or UnicodeCategory.MathSymbol
                or UnicodeCategory.CurrencySymbol
                or UnicodeCategory.ModifierSymbol
                or UnicodeCategory.OtherSymbol => true,
            _ => false,
        };
    }

    [GeneratedRegex(@"[\s\r\n\t、。．，,;；:：\[\]【】（）()<>＜＞""'`]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenSeparatorRegex();

    [GeneratedRegex(@"[a-zA-Z0-9][a-zA-Z0-9._:/+\-]{1,}", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiProductOrVersionRegex();

    [GeneratedRegex(@"\b\d{1,2}\.\d{1,2}(?:\.\d{1,3})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumberRegex();
}
