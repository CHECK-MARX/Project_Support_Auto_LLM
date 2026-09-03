using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Quality;
using SupportCaseManager.Ai.Core.Ranking;

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
        "権限不足",
        "権限不十分",
        "QAC",
        "Dashboard",
        "Validate",
        "インストール方法",
        "インストール",
        "アップロード",
        "Fiebie",
        "Fibe",
        "ファイル転送",
        "ダウンロードサイト",
        "ダウンロード",
        "アクセス",
        "Webフィルタ",
        "プロキシ",
        "SSL検査",
        "ブラウザ",
        "代替提供",
        "解析結果",
        "接続確認",
        "接続",
        "手順書",
        "手順",
        "方法",
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
        "最新リリース",
        "latest release",
        "最新パッチ",
        "latest patch",
        "リリース日",
        "release date",
        "サポート期限",
        "eol",
        "対応バージョン",
    ];

    private static readonly HashSet<string> StopWordSet = new(
        StopWords.Select(NormalizeTerm),
        StringComparer.Ordinal);

    public InquiryFocus Extract(
        string inquiryText,
        CaseContext? caseContext = null,
        bool usePhase175QualityControls = false)
    {
        if (string.IsNullOrWhiteSpace(inquiryText))
        {
            return new InquiryFocus();
        }

        var separated = TechnicalQueryExtractor.Separate(inquiryText, caseContext);
        var focusText = ExtractFocusText(string.IsNullOrWhiteSpace(separated.TechnicalText)
            ? inquiryText
            : separated.TechnicalText);
        var normalizedFocus = NormalizeText(focusText);
        var excludedTerms = FindExcludedTerms(normalizedFocus);
        var targetVersions = ExtractTargetVersions(focusText);
        var terms = ExtractImportantTerms(focusText, normalizedFocus, caseContext);
        var freshness = DetectFreshness(normalizedFocus);
        var topicAnalysis = usePhase175QualityControls
            ? NegationAwareTopicAnalyzer.Analyze(focusText, SupportTopicCatalog.Create(caseContext?.ProductName))
            : null;
        if (topicAnalysis is not null)
        {
            terms = terms
                .Where(term => !topicAnalysis.ExcludedTextSegments.Any(segment =>
                    segment.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                    topicAnalysis.PrimaryText.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return new InquiryFocus
        {
            FocusText = focusText.Trim(),
            ImportantTerms = targetVersions.Concat(terms).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList(),
            ExcludedTerms = excludedTerms,
            TargetVersions = targetVersions,
            IsFreshnessSensitive = freshness.IsSensitive,
            FreshnessReason = freshness.Reason,
            PrimaryTopics = topicAnalysis is null ? [] : ToReferences(topicAnalysis.PrimaryProfile),
            ExcludedTopics = topicAnalysis is null ? [] : ToReferences(topicAnalysis.ExcludedProfile),
            RequiredCoverage = topicAnalysis is null
                ? []
                : CoverageAnalyzer.Required(focusText, topicAnalysis.PrimaryProfile),
            RecipientContext = separated.RecipientContext,
            TechnicalQuery = TechnicalQueryExtractor.Extract(
                focusText,
                SupportTopicCatalog.Create(caseContext?.ProductName),
                topicAnalysis?.ExcludedProfile.Features
                    .Concat(topicAnalysis.ExcludedProfile.Operations)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? []),
        };
    }

    private static IReadOnlyList<InquiryTopicReference> ToReferences(TopicEntityProfile profile)
    {
        var values = new List<InquiryTopicReference>();
        values.AddRange(profile.Products.Select(static value => Topic("Product", value)));
        values.AddRange(profile.Components.Select(static value => Topic("Component", value)));
        values.AddRange(profile.Features.Select(static value => Topic("Feature", value)));
        values.AddRange(profile.Operations.Select(static value => Topic("Operation", value)));
        values.AddRange(profile.Entities.Select(static value => Topic(value.Kind.ToString(), value.Value)));
        return values
            .DistinctBy(static item => $"{item.Kind}|{item.Value}", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static InquiryTopicReference Topic(string kind, string value) => new()
    {
        Kind = kind,
        Value = value,
    };

    private static string ExtractFocusText(string inquiryText)
    {
        var normalized = inquiryText.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var offset = 0;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            foreach (var marker in SectionMarkers)
            {
                if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                {
                    var markerOffset = line.Length - trimmed.Length;
                    return normalized[(offset + markerOffset + marker.Length)..].Trim();
                }
            }

            offset += line.Length + 1;
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

        if (normalizedFocus.Contains(NormalizeTerm("権限"), StringComparison.Ordinal) &&
            normalizedFocus.Contains(NormalizeTerm("不十分"), StringComparison.Ordinal))
        {
            terms["権限不足"] = 120;
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

            if (ShouldKeepWholeToken(token))
            {
                terms[token] = Math.Max(terms.GetValueOrDefault(token), score);
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
            .Where(match => !LooksLikeNumberedStep(focusText, match) && !LooksLikeCalendarDate(match.Value))
            .Select(static match => match.Value.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static bool ShouldKeepWholeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            token.Contains('@') ||
            ContactTokenRegex().IsMatch(token))
        {
            return false;
        }

        if (ContainsJapanese(token) && token.Length > 24)
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikeNumberedStep(string text, Match match)
    {
        var before = match.Index > 0 ? text[match.Index - 1] : '\0';
        var afterIndex = match.Index + match.Length;
        var after = afterIndex < text.Length ? text[afterIndex] : '\0';
        return before is '[' or '［' or '(' or '（'
            && (char.IsWhiteSpace(after) || after is ']' or '］' or ')' or '）');
    }

    private static bool LooksLikeCalendarDate(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 3 &&
            parts[0].Length == 4 &&
            int.TryParse(parts[0], out var year) && year is >= 1900 and <= 2200 &&
            int.TryParse(parts[1], out var month) && month is >= 1 and <= 12 &&
            int.TryParse(parts[2], out var day) && day is >= 1 and <= 31;
    }

    private static (bool IsSensitive, string Reason) DetectFreshness(string normalizedFocus)
    {
        if (FreshnessIntentPolicy.IsOperationalAccessOrDeliveryInquiry(normalizedFocus))
        {
            return (false, string.Empty);
        }

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
            TechnicalBodySignalRegex().IsMatch(line) ||
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

    [GeneratedRegex(@"(?<![\d.])\d{1,4}(?:\.\d{1,4}){1,3}(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumberRegex();

    [GeneratedRegex(@"SQL\s*Injection|脆弱性|過検知|Sanitizer|False\s*Positive|Source|Sink|Query|Classic\s*ASP|Framework|Preset|解析|検出|クエリ|フレームワーク|添付", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalBodySignalRegex();

    [GeneratedRegex(@"(?i)(?:^|\b)(?:tel|e-?mail|fax)\b|〒|\d{2,4}-\d{2,4}-\d{3,4}", RegexOptions.CultureInvariant)]
    private static partial Regex ContactTokenRegex();
}
