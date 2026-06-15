using System.Globalization;
using System.Text;

namespace SupportCaseManager.Ai.Core.Search;

internal static class KeywordSearchScorer
{
    private const int JapaneseNGramMaxSourceLength = 80;
    private const int MaxStrongJapaneseTokenLength = 16;

    private static readonly string[] StopWords =
    [
        "よろしく",
        "よろしくお願いします",
        "お世話になっております",
        "お世話",
        "確認",
        "質問",
        "件名",
        "次に",
        "標準",
        "できますか",
        "でしょうか",
        "お願い",
        "please",
        "thanks",
        "thank",
        "確認したい",
        "教えて",
        "教えてください",
        "縺企｡倥＞縺励∪縺・",
        "繧医ｍ縺励￥",
        "縺贋ｸ冶ｩｱ",
        "遒ｺ隱・",
        "雉ｪ蝠・",
        "莉ｶ蜷・",
        "谺｡縺ｫ",
        "讓呵ｨ・",
        "縺ｧ縺阪∪縺吶°",
        "縺ｧ縺励ｇ縺・°",
    ];

    private static readonly string[] ImportantJapaneseTerms =
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
        "Validate",
        "アップロード",
        "プロジェクト",
        "QAC",
    ];

    private static readonly HashSet<string> StopWordSet = new(StopWords.Select(Normalize), StringComparer.Ordinal);
    private static readonly IReadOnlyList<string> ImportantJapaneseTermSet = ImportantJapaneseTerms
        .Select(Normalize)
        .Where(static term => !string.IsNullOrWhiteSpace(term))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    public static SearchScoreDetails Score(
        string query,
        IReadOnlyList<WeightedSearchField> fields)
    {
        var terms = ExtractTerms(query);
        if (terms.Count == 0)
        {
            return SearchScoreDetails.Empty;
        }

        var strongTerms = terms.Where(static term => !term.IsSupplemental).ToList();
        var coverageBase = strongTerms.Count > 0 ? strongTerms : terms;
        var matchedTerms = new Dictionary<string, double>(StringComparer.Ordinal);
        var weightedScore = 0.0;
        var titleMatches = 0;
        var bodyMatches = 0;
        var metadataMatches = 0;

        foreach (var field in fields)
        {
            var normalizedValue = Normalize(field.Value);
            if (string.IsNullOrWhiteSpace(normalizedValue))
            {
                continue;
            }

            foreach (var term in terms)
            {
                var occurrences = CountOccurrences(normalizedValue, term.Text);
                if (occurrences <= 0)
                {
                    continue;
                }

                var termContribution = term.Weight * field.Weight * Math.Min(3, occurrences);
                weightedScore += termContribution;
                matchedTerms[term.Text] = matchedTerms.TryGetValue(term.Text, out var current)
                    ? Math.Max(current, termContribution)
                    : termContribution;

                if (field.Kind == SearchFieldKind.Title)
                {
                    titleMatches += 1;
                }
                else if (field.Kind == SearchFieldKind.Metadata)
                {
                    metadataMatches += 1;
                }
                else
                {
                    bodyMatches += 1;
                }
            }
        }

        var covered = coverageBase.Count(term => matchedTerms.ContainsKey(term.Text));
        if (covered == 0)
        {
            return SearchScoreDetails.Empty;
        }

        var coverage = covered / (double)Math.Max(1, coverageBase.Count);
        var fieldStrength = Math.Min(1.0, weightedScore / Math.Max(4.0, coverageBase.Sum(static term => term.Weight) * 4.0));
        var score = (coverage * 0.62) + (fieldStrength * 0.28);

        if (titleMatches > 0)
        {
            score += 0.07;
        }

        if (metadataMatches > 0)
        {
            score += 0.05;
        }

        if (bodyMatches > 0 && titleMatches == 0 && metadataMatches == 0)
        {
            score -= 0.06;
            score = Math.Min(score, 0.70);
        }

        if (strongTerms.Count > 0 && covered < 2 && titleMatches == 0 && metadataMatches == 0)
        {
            score = Math.Min(score, 0.42);
        }

        score = Math.Clamp(score, 0.0, 1.0);
        return new SearchScoreDetails(
            Math.Round(score, 3),
            matchedTerms
                .OrderByDescending(static item => item.Value)
                .ThenBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => item.Key)
                .Take(12)
                .ToList(),
            $"{covered}/{coverageBase.Count}",
            $"coverage={coverage:0.00}; fieldStrength={fieldStrength:0.00}; title={titleMatches}; body={bodyMatches}; metadata={metadataMatches}");
    }

    private static IReadOnlyList<SearchTerm> ExtractTerms(string query)
    {
        var normalized = Normalize(query);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var terms = new Dictionary<string, SearchTerm>(StringComparer.Ordinal);
        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ContainsJapanese(token))
            {
                AddJapaneseSearchTerms(terms, token);
                continue;
            }

            AddTerm(terms, token, weight: token.Length >= 4 ? 1.0 : 0.75, isSupplemental: false);
        }

        return terms.Values
            .OrderByDescending(static term => term.Weight)
            .ThenByDescending(static term => term.Text.Length)
            .ThenBy(static term => term.Text, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddJapaneseSearchTerms(IDictionary<string, SearchTerm> terms, string token)
    {
        foreach (var importantTerm in ImportantJapaneseTermSet)
        {
            if (token.Contains(importantTerm, StringComparison.Ordinal))
            {
                AddTerm(terms, importantTerm, weight: importantTerm.Length >= 5 ? 1.35 : 1.1, isSupplemental: false);
            }
        }

        foreach (var segment in SplitJapaneseToken(token))
        {
            if (segment.Length <= MaxStrongJapaneseTokenLength)
            {
                AddTerm(terms, segment, weight: segment.Length >= 4 ? 1.0 : 0.8, isSupplemental: false);
            }

            if (segment.Length <= JapaneseNGramMaxSourceLength)
            {
                AddJapaneseNGrams(terms, segment);
            }
        }

        if (token.Length <= MaxStrongJapaneseTokenLength)
        {
            AddTerm(terms, token, weight: token.Length >= 4 ? 1.0 : 0.8, isSupplemental: false);
        }

        if (token.Length <= JapaneseNGramMaxSourceLength)
        {
            AddJapaneseNGrams(terms, token);
        }
    }

    private static IEnumerable<string> SplitJapaneseToken(string token)
    {
        var separators = new[]
        {
            "について",
            "したい",
            "できません",
            "できない",
            "ください",
            "します",
            "する",
            "です",
            "ます",
            "で",
            "が",
            "を",
            "は",
            "に",
            "へ",
            "と",
            "の",
            "も",
            "や",
        };

        var segments = new List<string> { token };
        foreach (var separator in separators)
        {
            segments = segments
                .SelectMany(segment => segment.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
        }

        return segments.Where(static segment => segment.Length >= 2);
    }

    private static void AddJapaneseNGrams(IDictionary<string, SearchTerm> terms, string token)
    {
        for (var length = 2; length <= 6; length++)
        {
            if (token.Length < length)
            {
                break;
            }

            for (var start = 0; start <= token.Length - length; start++)
            {
                var ngram = token.Substring(start, length);
                AddTerm(terms, ngram, weight: 0.25, isSupplemental: true);
            }
        }
    }

    private static void AddTerm(IDictionary<string, SearchTerm> terms, string term, double weight, bool isSupplemental)
    {
        if (term.Length < 2 || StopWordSet.Contains(term))
        {
            return;
        }

        if (terms.TryGetValue(term, out var existing))
        {
            if (weight > existing.Weight || (!isSupplemental && existing.IsSupplemental))
            {
                terms[term] = new SearchTerm(term, weight, isSupplemental && existing.IsSupplemental);
            }

            return;
        }

        terms[term] = new SearchTerm(term, weight, isSupplemental);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            builder.Append(IsSearchSeparator(ch) ? ' ' : ch);
        }

        return string.Join(
            " ",
            builder
                .ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool ContainsJapanese(string value)
    {
        return value.Any(static ch =>
            (ch >= '\u3040' && ch <= '\u30FF')
            || (ch >= '\u3400' && ch <= '\u9FFF'));
    }

    private static bool IsSearchSeparator(char ch)
    {
        if (char.IsWhiteSpace(ch))
        {
            return true;
        }

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

    private static int CountOccurrences(string text, string keyword)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(keyword, index, StringComparison.Ordinal)) >= 0)
        {
            count += 1;
            index += keyword.Length;
        }

        return count;
    }

    private sealed record SearchTerm(string Text, double Weight, bool IsSupplemental);
}

internal sealed record SearchScoreDetails(
    double Score,
    IReadOnlyList<string> MatchedTerms,
    string QueryCoverage,
    string ScoreBreakdown)
{
    public static SearchScoreDetails Empty { get; } = new(0, [], "0/0", string.Empty);
}

internal sealed record WeightedSearchField(string? Value, double Weight, SearchFieldKind Kind);

internal enum SearchFieldKind
{
    Title,
    Body,
    Metadata,
}
