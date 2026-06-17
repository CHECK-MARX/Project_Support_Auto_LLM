using SupportCaseManager.Ai.Contracts;

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

        var maxItems = request.Settings.MaxEvidenceItems > 0
            ? request.Settings.MaxEvidenceItems
            : DefaultMaxEvidenceItems;

        return request.Sources
            .OrderByDescending(static source => source.Score ?? 0)
            .ThenBy(static source => source.SourceId, StringComparer.Ordinal)
            .Take(maxItems)
            .Select(static source => new EvidenceItem
            {
                SourceId = source.SourceId,
                SourceType = source.SourceType,
                Title = source.Title,
                Excerpt = BuildExcerpt(source.Text),
                FilePath = source.FilePath,
                SupportNumber = source.SupportNumber,
                Relevance = Math.Clamp(source.Score ?? 0, 0, 1),
            })
            .ToList();
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
}
