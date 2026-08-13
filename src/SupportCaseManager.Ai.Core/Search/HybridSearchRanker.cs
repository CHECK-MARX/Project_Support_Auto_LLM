using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Llm;

namespace SupportCaseManager.Ai.Core.Search;

internal static partial class HybridSearchRanker
{
    public static async Task<IReadOnlyList<SearchSource>> RankWithEmbeddingsAsync(
        IReadOnlyList<SearchSource> sources,
        string query,
        string productName,
        string productIndexFolder,
        LlmProviderSettings providerSettings,
        IOllamaEmbeddingClient embeddingClient,
        int maxResults,
        CancellationToken cancellationToken)
    {
        try
        {
            var index = await EmbeddingIndexUpdater.LoadAsync(
                Path.Combine(productIndexFolder, EmbeddingIndexDocument.FileName),
                cancellationToken);
            if (index is null ||
                !string.Equals(index.EmbeddingModel, providerSettings.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
            {
                return Rank(sources, query, productName, maxResults);
            }

            var queryVectors = await embeddingClient.EmbedAsync(
                providerSettings.Endpoint,
                providerSettings.EmbeddingModel!,
                [query],
                cancellationToken);
            var queryVector = queryVectors.Single();
            var vectors = index.Entries.ToDictionary(
                static entry => $"{entry.SourceType}\n{entry.SourceId}",
                static entry => entry.Vector,
                StringComparer.Ordinal);
            var semanticScores = sources.ToDictionary(
                SourceKey,
                source => vectors.TryGetValue($"{source.SourceType}\n{source.SourceId}", out var vector)
                    ? CosineSimilarity(queryVector, vector)
                    : 0,
                StringComparer.Ordinal);
            return RankWithSemanticScores(sources, semanticScores, productName, maxResults, "embedding");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Rank(sources, query, productName, maxResults);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Rank(sources, query, productName, maxResults);
        }
    }

    public static IReadOnlyList<SearchSource> Rank(
        IReadOnlyList<SearchSource> sources,
        string query,
        string productName,
        int maxResults)
    {
        if (sources.Count == 0)
        {
            return [];
        }

        var semanticScores = sources.ToDictionary(
            SourceKey,
            source => Similarity(query, $"{source.Title} {source.Text}"),
            StringComparer.Ordinal);
        return RankWithSemanticScores(sources, semanticScores, productName, maxResults, "local");
    }

    private static IReadOnlyList<SearchSource> RankWithSemanticScores(
        IReadOnlyList<SearchSource> sources,
        IReadOnlyDictionary<string, double> semanticScores,
        string productName,
        int maxResults,
        string semanticMode)
    {
        if (sources.Count == 0)
        {
            return [];
        }

        var keywordRank = sources
            .OrderByDescending(static source => source.Score ?? 0)
            .Select((source, index) => (Key: SourceKey(source), Rank: index + 1))
            .ToDictionary(static item => item.Key, static item => item.Rank, StringComparer.Ordinal);
        var semanticRank = sources
            .Select(source => (Source: source, Score: semanticScores.GetValueOrDefault(SourceKey(source))))
            .OrderByDescending(static item => item.Score)
            .Select((item, index) => (Key: SourceKey(item.Source), Rank: index + 1, item.Score))
            .ToDictionary(static item => item.Key, static item => (item.Rank, item.Score), StringComparer.Ordinal);

        return sources
            .Select(source =>
            {
                var key = SourceKey(source);
                var lexical = keywordRank[key];
                var semantic = semanticRank[key];
                var rrf = (1d / (60 + lexical)) + (1d / (60 + semantic.Rank));
                var productBoost = string.IsNullOrWhiteSpace(source.ProductName) ||
                    string.Equals(source.ProductName, productName, StringComparison.OrdinalIgnoreCase)
                    ? 0.02
                    : -0.20;
                return source with
                {
                    Score = Math.Clamp((source.Score ?? 0) * 0.65 + semantic.Score * 0.25 + rrf * 3 + productBoost, 0, 1),
                    ScoreBreakdown = AppendBreakdown(source.ScoreBreakdown, semantic.Score, rrf, semanticMode),
                };
            })
            .Where(source => string.IsNullOrWhiteSpace(source.ProductName) ||
                string.Equals(source.ProductName, productName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static source => source.Score ?? 0)
            .Take(maxResults)
            .ToList();
    }

    private static string SourceKey(SearchSource source) => $"{source.SourceType}\n{source.SourceId}";

    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
        {
            return 0;
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        return leftMagnitude <= 0 || rightMagnitude <= 0
            ? 0
            : Math.Clamp(dot / Math.Sqrt(leftMagnitude * rightMagnitude), 0, 1);
    }

    private static double Similarity(string left, string right)
    {
        var leftTerms = Tokenize(left);
        var rightTerms = Tokenize(right);
        if (leftTerms.Count == 0 || rightTerms.Count == 0)
        {
            return 0;
        }

        var intersection = leftTerms.Intersect(rightTerms, StringComparer.Ordinal).Count();
        return intersection / Math.Sqrt(leftTerms.Count * rightTerms.Count);
    }

    private static HashSet<string> Tokenize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        return WordRegex().Matches(normalized)
            .Select(static match => match.Value)
            .Where(static term => term.Length > 1)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string AppendBreakdown(string? existing, double semantic, double rrf, string semanticMode)
    {
        var hybrid = $"Hybrid {semanticMode}={semantic:0.000}, rrf={rrf:0.000}";
        return string.IsNullOrWhiteSpace(existing) ? hybrid : $"{existing}; {hybrid}";
    }

    [GeneratedRegex(@"[\p{L}\p{N}_\-.]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
