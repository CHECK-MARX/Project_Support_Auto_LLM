using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Core.Search;

public sealed class AiCaseKeywordSearcher : IAiCaseKeywordSearcher
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

        var indexFilePath = Path.Combine(aiIndexFolder, AiCaseIndexBuilder.IndexFileName);
        if (!File.Exists(indexFilePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(indexFilePath);
        var document = await JsonSerializer.DeserializeAsync<AiIndexDocument>(stream, JsonOptions, cancellationToken);
        if (document?.Notes.Count is null or 0)
        {
            return [];
        }

        return document.Notes
            .Select(note => new ScoredNote(note, Score(note, query)))
            .Where(item => item.Score.Score > 0)
            .OrderByDescending(item => item.Score.Score)
            .ThenByDescending(item => item.Note.LastModifiedAt)
            .Take(maxResults)
            .Select(item => ToSearchSource(item.Note, item.Score, query))
            .ToList();
    }

    private static SearchSource ToSearchSource(AiIndexedNote note, SearchScoreDetails score, string query)
    {
        return new SearchSource
        {
            SourceId = note.Id,
            SourceType = "PastCaseNote",
            Title = note.Title,
            Text = BuildExcerpt(note.Text, query),
            FilePath = note.NoteFilePath,
            SupportNumber = note.SupportNumber,
            Score = score.Score,
            MatchedTerms = score.MatchedTerms,
            QueryCoverage = score.QueryCoverage,
            ScoreBreakdown = score.ScoreBreakdown,
            DocumentId = note.NoteFilePath,
            SectionTitle = note.NoteKind,
        };
    }

    private static SearchScoreDetails Score(AiIndexedNote note, string query)
    {
        return KeywordSearchScorer.Score(
            query,
            [
                new WeightedSearchField(note.SupportNumber, 5.0, SearchFieldKind.Metadata),
                new WeightedSearchField(note.Title, 3.4, SearchFieldKind.Title),
                new WeightedSearchField(note.NoteKind, 2.6, SearchFieldKind.Metadata),
                new WeightedSearchField(note.CompanyName, 1.6, SearchFieldKind.Metadata),
                new WeightedSearchField(note.Status, 1.2, SearchFieldKind.Metadata),
                new WeightedSearchField(note.Text, 1.0, SearchFieldKind.Body),
            ]);
    }

    private static string BuildExcerpt(string text, string query)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length <= SearchTextMaxLength)
        {
            return normalized;
        }

        var focusTerms = TopicEntityAnalyzer.Extract(query).Operations.Contains("Analysis", StringComparer.Ordinal)
            ? new[] { "qacli analyze", "qaclianalyze", "プロジェクトを解析", "解析を実行", "解析する", "run analysis" }
            : [];
        var matchIndex = focusTerms
            .Select(term => normalized.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(static index => index >= 0, -1);
        var start = matchIndex < 0
            ? 0
            : Math.Clamp(matchIndex - 220, 0, normalized.Length - SearchTextMaxLength);
        return $"{(start > 0 ? "..." : string.Empty)}{normalized.Substring(start, SearchTextMaxLength)}{(start + SearchTextMaxLength < normalized.Length ? "..." : string.Empty)}";
    }

    private sealed record ScoredNote(AiIndexedNote Note, SearchScoreDetails Score);
}
