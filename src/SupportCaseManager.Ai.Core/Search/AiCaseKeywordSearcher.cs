using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

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
            .Select(item => ToSearchSource(item.Note, item.Score))
            .ToList();
    }

    private static SearchSource ToSearchSource(AiIndexedNote note, SearchScoreDetails score)
    {
        return new SearchSource
        {
            SourceId = note.Id,
            SourceType = "PastCaseNote",
            Title = note.Title,
            Text = BuildExcerpt(note.Text),
            FilePath = note.NoteFilePath,
            SupportNumber = note.SupportNumber,
            Score = score.Score,
            MatchedTerms = score.MatchedTerms,
            QueryCoverage = score.QueryCoverage,
            ScoreBreakdown = score.ScoreBreakdown,
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

    private sealed record ScoredNote(AiIndexedNote Note, SearchScoreDetails Score);
}
