using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Search;

public sealed class AiOfficialDocumentKeywordSearcher : IAiOfficialDocumentKeywordSearcher
{
    private const int SearchTextMaxLength = 1200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<SearchSource>> SearchAsync(
        string productName,
        string indexFolder,
        InquiryFocus inquiryFocus,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productName) ||
            string.IsNullOrWhiteSpace(indexFolder) ||
            inquiryFocus is null ||
            string.IsNullOrWhiteSpace(inquiryFocus.FocusText) ||
            maxResults <= 0)
        {
            return [];
        }

        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(indexFolder, productName);
        var indexFilePath = Path.Combine(productIndexFolder, AiOfficialDocumentIndexBuilder.IndexFileName);
        if (!File.Exists(indexFilePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(indexFilePath);
        var document = await JsonSerializer.DeserializeAsync<AiOfficialDocumentIndexDocument>(stream, JsonOptions, cancellationToken);
        if (document?.Documents.Count is null or 0)
        {
            return [];
        }

        var query = BuildQuery(inquiryFocus);
        return document.Documents
            .Select(doc => new ScoredOfficialDocument(doc, Score(doc, query, inquiryFocus)))
            .Where(item => item.Score.Score > 0)
            .OrderByDescending(item => item.Score.Score)
            .ThenByDescending(item => item.Document.RetrievedAt)
            .ThenBy(item => item.Document.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(item => ToSearchSource(item.Document, item.Score))
            .ToList();
    }

    private static string BuildQuery(InquiryFocus inquiryFocus)
    {
        var parts = new List<string> { inquiryFocus.FocusText };
        parts.AddRange(inquiryFocus.ImportantTerms);
        parts.AddRange(inquiryFocus.TargetVersions);
        if (inquiryFocus.IsFreshnessSensitive)
        {
            parts.AddRange(
            [
                "最新",
                "最新バージョン",
                "version",
                "release",
                "EP",
                "HF",
                "Engine Pack",
                "Hotfix",
                "CxSAST",
            ]);
        }

        return string.Join(Environment.NewLine, parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static SearchScoreDetails Score(
        AiIndexedOfficialDocument document,
        string query,
        InquiryFocus inquiryFocus)
    {
        var fields = new List<WeightedSearchField>
        {
            new(document.Title, inquiryFocus.IsFreshnessSensitive ? 4.2 : 3.6, SearchFieldKind.Title),
            new(document.SectionTitle, 3.0, SearchFieldKind.Title),
            new(document.Url, 1.5, SearchFieldKind.Metadata),
            new(document.ProductName, 1.2, SearchFieldKind.Metadata),
            new(document.Text, inquiryFocus.IsFreshnessSensitive ? 1.8 : 1.0, SearchFieldKind.Body),
        };

        if (inquiryFocus.IsFreshnessSensitive)
        {
            fields.Add(new WeightedSearchField(
                "latest version release hotfix engine pack HF EP CxSAST 9.7",
                2.0,
                SearchFieldKind.Metadata));
        }

        var score = KeywordSearchScorer.Score(query, fields);
        if (score.Score <= 0 || inquiryFocus.TargetVersions.Count == 0)
        {
            return score;
        }

        var searchableText = string.Join(
            " ",
            document.Title,
            document.SectionTitle,
            document.Url,
            document.Text);
        var matchedVersions = inquiryFocus.TargetVersions
            .Where(version => searchableText.Contains(version, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matchedVersions.Count == 0)
        {
            return score;
        }

        return score with
        {
            Score = Math.Round(Math.Clamp(score.Score + 0.18, 0.0, 1.0), 3),
            MatchedTerms = matchedVersions
                .Concat(score.MatchedTerms)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList(),
            ScoreBreakdown = string.IsNullOrWhiteSpace(score.ScoreBreakdown)
                ? $"targetVersion={string.Join(",", matchedVersions)}"
                : $"{score.ScoreBreakdown}; targetVersion={string.Join(",", matchedVersions)}",
        };
    }

    private static SearchSource ToSearchSource(AiIndexedOfficialDocument document, SearchScoreDetails score)
    {
        return new SearchSource
        {
            SourceId = document.Id,
            SourceType = "OfficialDoc",
            Title = BuildTitle(document),
            Text = BuildExcerpt(document.Text),
            FilePath = null,
            Url = document.Url,
            RetrievedAt = document.RetrievedAt,
            SupportNumber = null,
            Score = score.Score,
            ProductName = document.ProductName,
            MatchedTerms = score.MatchedTerms,
            QueryCoverage = score.QueryCoverage,
            ScoreBreakdown = score.ScoreBreakdown,
        };
    }

    private static string BuildTitle(AiIndexedOfficialDocument document)
    {
        return string.IsNullOrWhiteSpace(document.SectionTitle)
            ? document.Title
            : $"{document.Title} - {document.SectionTitle}";
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

    private sealed record ScoredOfficialDocument(AiIndexedOfficialDocument Document, SearchScoreDetails Score);
}
