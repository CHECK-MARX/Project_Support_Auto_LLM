using System.Text.Json;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Ranking;

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
        var hasExplicitTargetVersion = inquiryFocus.TargetVersions.Count > 0;
        return document.Documents
            .Select(doc =>
            {
                var score = Score(doc, query, inquiryFocus);
                return new ScoredOfficialDocument(
                    doc,
                    score,
                    ProcedureSearchBoost.Calculate(query, doc.Title, doc.SectionTitle, doc.Url, doc.Text),
                    HasTargetVersion(doc, inquiryFocus.TargetVersions));
            })
            .Where(item => item.Score.Score > 0)
            .Where(item => !hasExplicitTargetVersion || item.TargetVersionMatched)
            .OrderByDescending(item => item.ProcedureSpecificity)
            .ThenByDescending(item => item.Score.Score)
            .ThenByDescending(item => item.Document.RetrievedAt)
            .ThenBy(item => item.Document.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(item => ToSearchSource(item.Document, item.Score, inquiryFocus))
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

        if (IsReleaseNotesQuery(inquiryFocus))
        {
            parts.AddRange(
            [
                "Release Notes",
                "release note",
                "released",
                "enhancement",
                "resolved issues",
                "what's new",
                "Engine Pack",
                "CxSAST",
                "SAST",
                "Checkmarx",
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
        var procedureBoost = ProcedureSearchBoost.Calculate(
            query,
            document.Title,
            document.SectionTitle,
            document.Url,
            document.Text);
        if (procedureBoost > 0)
        {
            score = score with
            {
                Score = Math.Round(ApplyBoundedBoost(score.Score, procedureBoost), 3),
                ScoreBreakdown = string.IsNullOrWhiteSpace(score.ScoreBreakdown)
                    ? $"procedureProximity={procedureBoost:0.00}"
                    : $"{score.ScoreBreakdown}; procedureProximity={procedureBoost:0.00}",
            };
        }

        var matchedVersions = FindMatchedTargetVersions(document, inquiryFocus.TargetVersions);
        if (score.Score <= 0 && matchedVersions.Count == 0)
        {
            return score;
        }

        if (score.Score <= 0)
        {
            score = new SearchScoreDetails(
                0.48,
                matchedVersions,
                $"{matchedVersions.Count}/{Math.Max(1, inquiryFocus.TargetVersions.Count)} target versions",
                "version-only-match=true");
        }

        var exactVersionHeading = HasVersionMatch(
            string.Join(" ", document.Title, document.SectionTitle),
            matchedVersions);
        var releasePage = IsReleasePage(document);
        var boost = exactVersionHeading
            ? (IsReleaseNotesQuery(inquiryFocus) ? 0.34 : 0.22)
            : releasePage && IsReleaseNotesQuery(inquiryFocus)
                ? 0.18
                : 0.08;
        var breakdown = string.IsNullOrWhiteSpace(score.ScoreBreakdown)
            ? $"targetVersion={string.Join(",", matchedVersions)}"
            : $"{score.ScoreBreakdown}; targetVersion={string.Join(",", matchedVersions)}";
        if (exactVersionHeading)
        {
            breakdown += "; exactVersionHeading=true";
        }

        return score with
        {
            Score = Math.Round(ApplyBoundedBoost(score.Score, boost), 3),
            MatchedTerms = matchedVersions
                .Concat(score.MatchedTerms)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList(),
            ScoreBreakdown = breakdown,
        };
    }

    private static bool IsReleaseNotesQuery(InquiryFocus inquiryFocus)
    {
        return inquiryFocus.TechnicalQuery.Intent.Contains("ReleaseNotes", StringComparer.OrdinalIgnoreCase) ||
            ContainsAny(
                inquiryFocus.FocusText,
                "リリースノート", "リリース内容", "変更内容", "変更点", "追加機能", "新機能", "修正内容", "対応内容", "バージョン情報",
                "release notes", "release note", "released", "enhancement", "resolved issues", "what's new", "engine pack");
    }

    private static bool HasTargetVersion(
        AiIndexedOfficialDocument document,
        IReadOnlyList<string> targetVersions)
    {
        return FindMatchedTargetVersions(document, targetVersions).Count > 0;
    }

    private static IReadOnlyList<string> FindMatchedTargetVersions(
        AiIndexedOfficialDocument document,
        IReadOnlyList<string> targetVersions)
    {
        if (targetVersions.Count == 0)
        {
            return [];
        }

        var searchableText = string.Join(
            " ",
            document.Title,
            document.SectionTitle,
            document.Url,
            document.Text);
        return targetVersions
            .Where(version => HasVersionMatch(searchableText, version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasVersionMatch(string text, IEnumerable<string> versions)
    {
        return versions.Any(version => HasVersionMatch(text, version));
    }

    private static bool HasVersionMatch(string text, string version)
    {
        var parts = version
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(part => !part.All(char.IsDigit)))
        {
            return false;
        }

        var optionalPatch = parts.Length == 2 ? @"(?:[.\-]\d+)?" : string.Empty;
        var pattern = $"(?<![\\d.]){string.Join(@"[.\\-_/\\s]+", parts.Select(Regex.Escape))}{optionalPatch}(?!\\d)(?!\\.\\d)";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsReleasePage(AiIndexedOfficialDocument document)
    {
        return ContainsAny(
            string.Join(" ", document.Title, document.SectionTitle, document.Url),
            "release notes", "release-note", "resolved issues", "what's new", "engine pack", "hotfix");
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static SearchSource ToSearchSource(
        AiIndexedOfficialDocument document,
        SearchScoreDetails score,
        InquiryFocus inquiryFocus)
    {
        return new SearchSource
        {
            SourceId = document.Id,
            SourceType = "OfficialDoc",
            Title = BuildTitle(document),
            Text = BuildExcerpt(document.Text, inquiryFocus, document.ProductName),
            FilePath = null,
            Url = document.Url,
            RetrievedAt = document.RetrievedAt,
            SupportNumber = null,
            Score = score.Score,
            ProductName = document.ProductName,
            MatchedTerms = score.MatchedTerms,
            QueryCoverage = score.QueryCoverage,
            ScoreBreakdown = score.ScoreBreakdown,
            DocumentId = document.Url,
            SectionTitle = document.SectionTitle,
            DocumentTitle = document.Title,
            ChunkId = document.Id,
        };
    }

    private static string BuildTitle(AiIndexedOfficialDocument document)
    {
        return string.IsNullOrWhiteSpace(document.SectionTitle)
            ? document.Title
            : $"{document.Title} - {document.SectionTitle}";
    }

    private static string BuildExcerpt(string text, InquiryFocus inquiryFocus, string productName)
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

        var catalog = SupportTopicCatalog.Create(productName);
        var profile = TopicEntityAnalyzer.Extract(inquiryFocus.FocusText, catalog);
        var focusTerms = catalog.Features
            .Where(feature => profile.Features.Contains(
                feature.CanonicalName,
                StringComparer.OrdinalIgnoreCase))
            .SelectMany(feature => new[] { feature.CanonicalName }.Concat(feature.Aliases))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (profile.Operations.Contains("Analysis", StringComparer.Ordinal))
        {
            focusTerms.InsertRange(0, ["qacli analyze", "qaclianalyze", "analyze project", "run analysis", "project analysis", "解析を実行"]);
        }

        var matchIndex = focusTerms
            .Select(term => normalized.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(static index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (matchIndex < 0)
        {
            return normalized[..SearchTextMaxLength] + "...";
        }

        var startIndex = Math.Max(0, matchIndex - 240);
        if (startIndex + SearchTextMaxLength > normalized.Length)
        {
            startIndex = normalized.Length - SearchTextMaxLength;
        }

        var prefix = startIndex > 0 ? "..." : string.Empty;
        var suffix = startIndex + SearchTextMaxLength < normalized.Length ? "..." : string.Empty;
        return $"{prefix}{normalized.Substring(startIndex, SearchTextMaxLength)}{suffix}";
    }

    private static double ApplyBoundedBoost(double score, double boost)
    {
        var normalized = Math.Clamp(score, 0, 1);
        return normalized + ((1 - normalized) * Math.Clamp(boost, 0, 0.95));
    }

    private sealed record ScoredOfficialDocument(
        AiIndexedOfficialDocument Document,
        SearchScoreDetails Score,
        double ProcedureSpecificity,
        bool TargetVersionMatched);
}
