using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;

namespace SupportCaseManager.Ai.Core.Evidence;

public static class EvidenceSourceSelector
{
    public static IReadOnlyList<SearchSource> Select(
        IReadOnlyList<SearchSource> sources,
        CaseContext caseContext,
        FactResolutionResult factResolution,
        int maxItems,
        int maxPromptChars)
    {
        var questionTypes = factResolution.Classification.QuestionTypes;
        var budget = Math.Max(600, maxPromptChars / 2);
        var selected = new List<SearchSource>();
        var titleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urlCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var usedChars = 0;

        foreach (var source in sources
            .Where(source => IsRelevant(source, caseContext, questionTypes))
            .OrderBy(source => Priority(source.SourceType, questionTypes))
            .ThenByDescending(static source => source.Score ?? 0))
        {
            if (selected.Count >= Math.Max(1, maxItems))
            {
                break;
            }

            var titleKey = source.Title.Trim();
            if (!string.IsNullOrWhiteSpace(titleKey) && !titleKeys.Add(titleKey))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(source.Url))
            {
                var url = source.Url.Trim().TrimEnd('/');
                var count = urlCounts.GetValueOrDefault(url);
                if (count >= 1)
                {
                    continue;
                }

                urlCounts[url] = count + 1;
            }

            if (selected.Any(existing => Similarity(existing.Text, source.Text) >= 0.88))
            {
                continue;
            }

            var sourceChars = source.Title.Length + source.Text.Length;
            if (selected.Count > 0 && usedChars + sourceChars > budget)
            {
                continue;
            }

            selected.Add(source);
            usedChars += sourceChars;
        }

        return selected;
    }

    private static bool IsRelevant(
        SearchSource source,
        CaseContext context,
        IReadOnlyList<string> questionTypes)
    {
        if (!string.IsNullOrWhiteSpace(source.ProductName) &&
            !string.IsNullOrWhiteSpace(context.ProductName) &&
            !string.Equals(source.ProductName, context.ProductName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !questionTypes.Contains(QuestionTypes.LatestVersionQuestion, StringComparer.OrdinalIgnoreCase)
            || !IsPastSource(source.SourceType);
    }

    private static int Priority(string sourceType, IReadOnlyList<string> questionTypes)
    {
        if (string.Equals(sourceType, "CuratedFact", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (questionTypes.Contains(QuestionTypes.TroubleshootingQuestion, StringComparer.OrdinalIgnoreCase))
        {
            return sourceType switch
            {
                "ExactPastAnswer" => 1,
                "Manual" => 2,
                "OfficialDoc" => 3,
                "PastAnswer" => 4,
                "PastCaseNote" => 5,
                _ => 6,
            };
        }

        if (questionTypes.Contains(QuestionTypes.HowToQuestion, StringComparer.OrdinalIgnoreCase))
        {
            return sourceType switch
            {
                "Manual" => 1,
                "OfficialDoc" => 2,
                "ExactPastAnswer" => 3,
                "PastAnswer" => 4,
                "PastCaseNote" => 5,
                _ => 6,
            };
        }

        return sourceType switch
        {
            "OfficialDoc" => 1,
            "Manual" => 2,
            "ExactPastAnswer" => 3,
            "PastAnswer" => 4,
            "PastCaseNote" => 5,
            _ => 6,
        };
    }

    private static bool IsPastSource(string? sourceType)
    {
        return sourceType is "ExactPastAnswer" or "PastAnswer" or "PastCaseNote";
    }

    private static double Similarity(string left, string right)
    {
        var leftTerms = Terms(left);
        var rightTerms = Terms(right);
        if (leftTerms.Count == 0 || rightTerms.Count == 0)
        {
            return 0;
        }

        var intersection = leftTerms.Intersect(rightTerms, StringComparer.OrdinalIgnoreCase).Count();
        var union = leftTerms.Union(rightTerms, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : intersection / (double)union;
    }

    private static HashSet<string> Terms(string value)
    {
        return value
            .Split([' ', '\r', '\n', '\t', '。', '、', ',', '.', ':', ';', '/', '\\', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static term => term.Length > 1)
            .Take(300)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
