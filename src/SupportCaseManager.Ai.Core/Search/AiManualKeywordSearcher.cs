using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Search;

public sealed class AiManualKeywordSearcher : IAiManualKeywordSearcher
{
    private const int SearchTextMaxLength = 1200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<SearchSource>> SearchAsync(
        string aiIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(aiIndexFolder) || string.IsNullOrWhiteSpace(query) || maxResults <= 0)
        {
            return [];
        }

        var indexFilePath = Path.Combine(aiIndexFolder, AiManualIndexBuilder.IndexFileName);
        if (!File.Exists(indexFilePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(indexFilePath);
        var document = await JsonSerializer.DeserializeAsync<AiManualIndexDocument>(stream, JsonOptions, cancellationToken);
        if (document?.Manuals.Count is null or 0)
        {
            return [];
        }

        return document.Manuals
            .Select(manual => new ScoredManual(manual, Score(manual, query)))
            .Where(item => item.Score.Score > 0)
            .OrderByDescending(item => item.Score.Score)
            .ThenBy(item => item.Manual.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Manual.SectionTitle, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(item => ToSearchSource(item.Manual, item.Score))
            .ToList();
    }

    private static SearchSource ToSearchSource(AiIndexedManual manual, SearchScoreDetails score)
    {
        return new SearchSource
        {
            SourceId = manual.Id,
            SourceType = "Manual",
            Title = manual.Title,
            Text = BuildExcerpt(manual.Text),
            FilePath = manual.FilePath,
            SupportNumber = null,
            Score = score.Score,
            MatchedTerms = score.MatchedTerms,
            QueryCoverage = score.QueryCoverage,
            ScoreBreakdown = score.ScoreBreakdown,
        };
    }

    private static SearchScoreDetails Score(AiIndexedManual manual, string query)
    {
        return KeywordSearchScorer.Score(
            query,
            [
                new WeightedSearchField(manual.Title, 3.4, SearchFieldKind.Title),
                new WeightedSearchField(manual.SectionTitle, 3.0, SearchFieldKind.Title),
                new WeightedSearchField(manual.FileName, 2.0, SearchFieldKind.Metadata),
                new WeightedSearchField(manual.Text, 1.0, SearchFieldKind.Body),
            ]);
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
        return normalized.Length <= SearchTextMaxLength
            ? normalized
            : normalized[..SearchTextMaxLength] + "...";
    }

    private sealed record ScoredManual(AiIndexedManual Manual, SearchScoreDetails Score);
}
