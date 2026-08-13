using System.Text.Json;
using System.Text;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Search;

public sealed class CaseAnswerPairSearcher : ICaseAnswerPairSearcher
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<SearchSource>> SearchAsync(
        string productIndexFolder,
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
        {
            return [];
        }

        var document = await ReadIndexAsync(productIndexFolder, cancellationToken);
        if (document?.Pairs.Count is null or 0)
        {
            return [];
        }

        var rawQuery = NormalizeRaw(query);
        var normalizedQuery = PastQuestionNormalizer.Normalize(query);
        var questionHash = PastQuestionNormalizer.Hash(normalizedQuery);
        return document.Pairs
            .Select(pair => Score(pair, rawQuery, normalizedQuery, questionHash))
            .Where(static match => match.Score > 0)
            .OrderBy(static match => match.Order)
            .ThenByDescending(static match => match.Score)
            .ThenByDescending(static match => match.Pair.UpdatedAt)
            .Take(maxResults)
            .Select(static match => ToSearchSource(match))
            .ToList();
    }

    public async Task<IReadOnlyList<SearchSource>> SearchBySupportNumberAsync(
        string productIndexFolder,
        string supportNumber,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(supportNumber) || maxResults <= 0)
        {
            return [];
        }

        var document = await ReadIndexAsync(productIndexFolder, cancellationToken);
        if (document?.Pairs.Count is null or 0)
        {
            return [];
        }

        var normalizedSupportNumber = supportNumber.Trim();
        return document.Pairs
            .Where(pair => string.Equals(
                pair.SupportNumber.Trim(),
                normalizedSupportNumber,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static pair => pair.UpdatedAt)
            .Take(maxResults)
            .Select(static pair => ToSearchSource(
                new ScoredPair(pair, 1, 0, PastAnswerMatchKinds.SupportNumber)))
            .ToList();
    }

    private static ScoredPair Score(
        CaseAnswerPair pair,
        string rawQuery,
        string normalizedQuery,
        string questionHash)
    {
        if (string.Equals(NormalizeRaw(pair.QuestionText), rawQuery, StringComparison.Ordinal))
        {
            return new ScoredPair(pair, 1, 1, PastAnswerMatchKinds.Exact);
        }

        if (string.Equals(pair.NormalizedQuestion, normalizedQuery, StringComparison.Ordinal))
        {
            return new ScoredPair(pair, 0.99, 2, PastAnswerMatchKinds.NormalizedExact);
        }

        if (string.Equals(pair.QuestionHash, questionHash, StringComparison.OrdinalIgnoreCase))
        {
            return new ScoredPair(pair, 0.985, 3, PastAnswerMatchKinds.HashExact);
        }

        var similarity = PastQuestionNormalizer.Similarity(pair.NormalizedQuestion, normalizedQuery);
        if (similarity >= 0.72)
        {
            return new ScoredPair(pair, Math.Clamp(0.70 + similarity * 0.28, 0, 0.98), 4, PastAnswerMatchKinds.NearDuplicate);
        }

        var keyword = KeywordSearchScorer.Score(
            normalizedQuery,
            [
                new WeightedSearchField(pair.QuestionText, 4.0, SearchFieldKind.Title),
                new WeightedSearchField(pair.CustomerReplyText, 1.0, SearchFieldKind.Body),
            ]);
        return keyword.Score > 0
            ? new ScoredPair(pair, Math.Min(0.84, keyword.Score), 5, PastAnswerMatchKinds.Keyword)
            : new ScoredPair(pair, 0, 6, PastAnswerMatchKinds.None);
    }

    private static SearchSource ToSearchSource(ScoredPair match)
    {
        var exactOrNear = match.Order == 0 || match.Order <= 4 && match.Score >= 0.90;
        return new SearchSource
        {
            SourceId = match.Pair.Id,
            SourceType = exactOrNear ? "ExactPastAnswer" : "PastAnswer",
            ProductName = match.Pair.ProductName,
            Title = BuildTitle(match.Pair.QuestionText),
            Text = match.Pair.CustomerReplyText,
            FilePath = match.Pair.SourceFile,
            SupportNumber = match.Pair.SupportNumber,
            RetrievedAt = match.Pair.UpdatedAt,
            Score = match.Score,
            ScoreBreakdown = $"PastAnswer {match.Kind}={match.Score:0.000}",
            QueryCoverage = match.Kind,
            QuestionText = match.Pair.QuestionText,
            InternalMemo = match.Pair.InternalMemo,
            MatchKind = match.Kind,
            DocumentId = match.Pair.SourceFile,
            DocumentTitle = BuildTitle(match.Pair.QuestionText),
            ChunkId = match.Pair.Id,
            SectionTitle = match.Kind,
        };
    }

    private static string NormalizeRaw(string value)
    {
        return string.Join(
            '\n',
            value.Normalize(NormalizationForm.FormKC)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.TrimEntries))
            .Trim();
    }

    private static string BuildTitle(string question)
    {
        var oneLine = string.Join(' ', question.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return oneLine.Length <= 100 ? $"過去回答: {oneLine}" : $"過去回答: {oneLine[..100]}...";
    }

    private sealed record ScoredPair(CaseAnswerPair Pair, double Score, int Order, string Kind);

    private static async Task<CaseAnswerPairIndexDocument?> ReadIndexAsync(
        string productIndexFolder,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(productIndexFolder, CaseAnswerPairIndexDocument.FileName);
        if (!File.Exists(indexPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(indexPath);
        return await JsonSerializer.DeserializeAsync<CaseAnswerPairIndexDocument>(stream, JsonOptions, cancellationToken);
    }
}

public static class PastAnswerMatchKinds
{
    public const string None = "None";
    public const string Exact = "Exact";
    public const string NormalizedExact = "NormalizedExact";
    public const string HashExact = "QuestionHash";
    public const string NearDuplicate = "NearDuplicate";
    public const string Keyword = "Keyword";
    public const string SupportNumber = "SupportNumber";
}
