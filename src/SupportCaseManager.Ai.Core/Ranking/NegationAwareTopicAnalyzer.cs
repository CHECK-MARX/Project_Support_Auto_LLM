using System.Text;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Ranking;

public sealed record NegationAwareTopicAnalysis
{
    public TopicEntityProfile PrimaryProfile { get; init; } = new();
    public TopicEntityProfile ExcludedProfile { get; init; } = new();
    public string PrimaryText { get; init; } = string.Empty;
    public IReadOnlyList<string> ExcludedTextSegments { get; init; } = [];
}

public static partial class NegationAwareTopicAnalyzer
{
    public static NegationAwareTopicAnalysis Analyze(string? text, TopicEntityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new NegationAwareTopicAnalysis();
        }

        var masked = new StringBuilder(text);
        var segments = new List<string>();
        foreach (Match marker in ExclusionMarkerRegex().Matches(text))
        {
            var start = FindClauseStart(text, marker.Index);
            if (marker.Index <= start)
            {
                continue;
            }

            var segment = text[start..marker.Index].Trim(' ', '、', ',', '。');
            if (!string.IsNullOrWhiteSpace(segment))
            {
                segments.Add(segment);
            }

            for (var index = start; index < marker.Index + marker.Length; index++)
            {
                masked[index] = ' ';
            }
        }

        var excluded = Merge(segments.Select(segment => TopicEntityAnalyzer.Extract(segment, catalog)));
        var primaryText = masked.ToString();
        return new NegationAwareTopicAnalysis
        {
            PrimaryProfile = TopicEntityAnalyzer.Extract(primaryText, catalog),
            ExcludedProfile = excluded,
            PrimaryText = primaryText,
            ExcludedTextSegments = segments,
        };
    }

    public static bool Overlaps(TopicEntityProfile excluded, TopicEntityProfile candidate)
    {
        ArgumentNullException.ThrowIfNull(excluded);
        ArgumentNullException.ThrowIfNull(candidate);
        return Intersects(excluded.Products, candidate.Products) ||
            Intersects(excluded.Components, candidate.Components) ||
            Intersects(excluded.Features, candidate.Features) ||
            Intersects(excluded.Objects, candidate.Objects) ||
            excluded.Entities.Any(left => candidate.Entities.Any(right =>
                left.Kind == right.Kind && left.NormalizedValue == right.NormalizedValue));
    }

    private static int FindClauseStart(string text, int markerIndex)
    {
        for (var index = markerIndex - 1; index >= 0; index--)
        {
            if (text[index] is '。' or '.' or '！' or '？' or '!' or '?' or '\n' or '\r' or ';' or '；')
            {
                return index + 1;
            }
        }
        return 0;
    }

    private static TopicEntityProfile Merge(IEnumerable<TopicEntityProfile> profiles)
    {
        var values = profiles.ToList();
        return new TopicEntityProfile
        {
            Products = Distinct(values.SelectMany(static item => item.Products)),
            Components = Distinct(values.SelectMany(static item => item.Components)),
            Features = Distinct(values.SelectMany(static item => item.Features)),
            Operations = Distinct(values.SelectMany(static item => item.Operations)),
            Objects = Distinct(values.SelectMany(static item => item.Objects)),
            Intents = Distinct(values.SelectMany(static item => item.Intents)),
            Entities = values.SelectMany(static item => item.Entities)
                .DistinctBy(static item => (item.Kind, item.NormalizedValue))
                .ToList(),
        };
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values) =>
        values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool Intersects(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var normalized = right.Select(TopicEntityAnalyzer.NormalizeText).ToHashSet(StringComparer.Ordinal);
        return left.Any(value => normalized.Contains(TopicEntityAnalyzer.NormalizeText(value)));
    }

    [GeneratedRegex("ではなく(?:て)?|ではない|ではありません|以外(?:は|の)?|を除く|対象外(?:です|とする)?|(?:is|are)\\s+excluded|excluding|except(?:\\s+for)?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ExclusionMarkerRegex();
}
