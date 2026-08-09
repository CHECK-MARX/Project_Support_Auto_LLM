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
            .Select(manual => new ScoredManual(
                manual,
                Score(manual, query),
                ProcedureSearchBoost.Calculate(query, manual.Title, manual.SectionTitle, manual.FileName, manual.Text)))
            .Where(item => item.Score.Score > 0)
            .OrderByDescending(item => item.ProcedureSpecificity)
            .ThenByDescending(item => item.Score.Score)
            .ThenBy(item => item.Manual.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Manual.SectionTitle, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(item => ToSearchSource(item.Manual, item.Score, query))
            .ToList();
    }

    private static SearchSource ToSearchSource(AiIndexedManual manual, SearchScoreDetails score, string query)
    {
        return new SearchSource
        {
            SourceId = manual.Id,
            SourceType = "Manual",
            Title = manual.Title,
            Text = BuildExcerpt(manual.Text, query, score.MatchedTerms),
            FilePath = manual.FilePath,
            SupportNumber = null,
            Score = score.Score,
            MatchedTerms = score.MatchedTerms,
            QueryCoverage = score.QueryCoverage,
            ScoreBreakdown = score.ScoreBreakdown,
            DocumentId = manual.ArchivePath ?? manual.FilePath,
            SectionTitle = manual.SectionTitle,
            ContentHash = manual.Sha256,
        };
    }

    private static SearchScoreDetails Score(AiIndexedManual manual, string query)
    {
        var score = KeywordSearchScorer.Score(
            query,
            [
                new WeightedSearchField(manual.Title, 3.4, SearchFieldKind.Title),
                new WeightedSearchField(manual.SectionTitle, 3.0, SearchFieldKind.Title),
                new WeightedSearchField(manual.FileName, 2.0, SearchFieldKind.Metadata),
                new WeightedSearchField(manual.Text, 1.0, SearchFieldKind.Body),
            ]);
        var procedureBoost = ProcedureSearchBoost.Calculate(
            query,
            manual.Title,
            manual.SectionTitle,
            manual.FileName,
            manual.Text);
        if (procedureBoost > 0)
        {
            score = score with
            {
                Score = Math.Round(Math.Clamp(score.Score + procedureBoost, 0, 1), 3),
                ScoreBreakdown = string.IsNullOrWhiteSpace(score.ScoreBreakdown)
                    ? $"procedureProximity={procedureBoost:0.00}"
                    : $"{score.ScoreBreakdown}; procedureProximity={procedureBoost:0.00}",
            };
        }

        var tableOfContentsPenalty = SearchDocumentQuality.CalculateTableOfContentsPenalty(manual.Text);
        return tableOfContentsPenalty <= 0
            ? score
            : score with
            {
                Score = Math.Round(Math.Max(0, score.Score - tableOfContentsPenalty), 3),
                ScoreBreakdown = string.IsNullOrWhiteSpace(score.ScoreBreakdown)
                    ? $"tableOfContentsPenalty=-{tableOfContentsPenalty:0.00}"
                    : $"{score.ScoreBreakdown}; tableOfContentsPenalty=-{tableOfContentsPenalty:0.00}",
            };
    }

    private static string BuildExcerpt(string text, string query, IReadOnlyList<string> matchedTerms)
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

        var candidates = new[] { "解析結果をアップロード", "アップロード", "Validate", "GUI", "CLI" }
            .Where(term => query.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Concat(matchedTerms.OrderByDescending(static term => term.Length))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var matchIndex = candidates
            .Select(term => normalized.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(static index => index >= 0, -1);
        var start = matchIndex < 0
            ? 0
            : Math.Clamp(matchIndex - 220, 0, normalized.Length - SearchTextMaxLength);
        var excerpt = normalized.Substring(start, SearchTextMaxLength);
        return $"{(start > 0 ? "..." : string.Empty)}{excerpt}{(start + excerpt.Length < normalized.Length ? "..." : string.Empty)}";
    }

    private sealed record ScoredManual(
        AiIndexedManual Manual,
        SearchScoreDetails Score,
        double ProcedureSpecificity);
}
