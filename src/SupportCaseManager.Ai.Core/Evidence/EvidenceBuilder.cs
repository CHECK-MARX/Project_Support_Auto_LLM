using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;

namespace SupportCaseManager.Ai.Core.Evidence;

public sealed class EvidenceBuilder : IEvidenceBuilder
{
    private const int DefaultMaxEvidenceItems = 2;
    private const int ExcerptMaxLength = 240;

    public IReadOnlyList<EvidenceItem> BuildEvidence(AnswerDraftRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.Settings.UseCoverageAwareEvidenceSelection)
        {
            return request.Sources.Select(ToEvidenceItem).ToList();
        }

        var maxItems = request.Settings.MaxEvidenceItems > 0
            ? request.Settings.MaxEvidenceItems
            : DefaultMaxEvidenceItems;

        var questionTypes = request.FactResolution?.Classification.QuestionTypes ?? [];
        var charBudget = Math.Max(600, request.Settings.MaxPromptChars / 2);
        var selected = new List<SearchSource>();
        var urlCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedChars = 0;

        foreach (var source in request.Sources
            .Where(source => IsRelevantSource(source, request, questionTypes))
            .OrderBy(source => SourcePriority(source.SourceType, questionTypes))
            .ThenByDescending(static source => source.Score ?? 0))
        {
            if (selected.Count >= maxItems)
            {
                break;
            }

            var normalizedTitle = source.Title.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedTitle) && !titles.Add(normalizedTitle))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(source.Url))
            {
                var normalizedUrl = NormalizeUrl(source.Url);
                var count = urlCounts.GetValueOrDefault(normalizedUrl);
                if (count >= 1)
                {
                    continue;
                }

                urlCounts[normalizedUrl] = count + 1;
            }

            var excerpt = BuildExcerpt(source.Text);
            if (selected.Any(existing => IsNearDuplicate(existing.Text, source.Text)))
            {
                continue;
            }

            var itemChars = source.Title.Length + excerpt.Length;
            if (selected.Count > 0 && usedChars + itemChars > charBudget)
            {
                continue;
            }

            selected.Add(source);
            usedChars += itemChars;
        }

        return selected.Select(ToEvidenceItem).ToList();
    }

    public double CalculateConfidence(AnswerDraftRequest request, IReadOnlyList<EvidenceItem> evidence)
    {
        if (evidence.Count == 0)
        {
            return 0.0;
        }

        var averageRelevance = evidence.Average(static item => item.Relevance);
        var countBoost = Math.Min(0.2, (evidence.Count - 1) * 0.05);
        var relevancePart = averageRelevance > 0 ? averageRelevance * 0.45 : 0;
        var confidence = 0.4 + relevancePart + countBoost;
        return Math.Round(Math.Clamp(confidence, 0, 1), 2);
    }

    private static string BuildExcerpt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length <= ExcerptMaxLength
            ? normalized
            : normalized[..ExcerptMaxLength] + "...";
    }

    private static EvidenceItem ToEvidenceItem(SearchSource source) => new()
    {
        SourceId = source.SourceId,
        SourceType = source.SourceType,
        Title = source.Title,
        Excerpt = BuildExcerpt(source.Text),
        FilePath = source.FilePath,
        SupportNumber = source.SupportNumber,
        Relevance = Math.Clamp(source.Score ?? 0, 0, 1),
    };

    private static bool IsRelevantSource(
        SearchSource source,
        AnswerDraftRequest request,
        IReadOnlyList<string> questionTypes)
    {
        if (!string.IsNullOrWhiteSpace(source.ProductName) &&
            !string.IsNullOrWhiteSpace(request.Case.ProductName) &&
            !string.Equals(source.ProductName, request.Case.ProductName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !questionTypes.Contains(QuestionTypes.LatestVersionQuestion, StringComparer.OrdinalIgnoreCase)
            || !IsPastSource(source.SourceType);
    }

    private static int SourcePriority(string? sourceType, IReadOnlyList<string> questionTypes)
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

    private static string NormalizeUrl(string value) => value.Trim().TrimEnd('/');

    private static bool IsNearDuplicate(string left, string right)
    {
        var leftTerms = Tokenize(left);
        var rightTerms = Tokenize(right);
        if (leftTerms.Count == 0 || rightTerms.Count == 0)
        {
            return false;
        }

        var intersection = leftTerms.Intersect(rightTerms, StringComparer.OrdinalIgnoreCase).Count();
        var union = leftTerms.Union(rightTerms, StringComparer.OrdinalIgnoreCase).Count();
        return union > 0 && intersection / (double)union >= 0.88;
    }

    private static HashSet<string> Tokenize(string value)
    {
        return value
            .Split([' ', '\r', '\n', '\t', '。', '、', ',', '.', ':', ';', '/', '\\', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static term => term.Length > 1)
            .Take(200)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
