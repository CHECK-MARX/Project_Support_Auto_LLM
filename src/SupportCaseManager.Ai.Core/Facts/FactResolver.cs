using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Facts;

public sealed class FactResolver : IFactResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IQuestionClassifier questionClassifier;
    private readonly IOfficialDocumentFactExtractor officialDocumentFactExtractor;

    public FactResolver(
        IQuestionClassifier? questionClassifier = null,
        IOfficialDocumentFactExtractor? officialDocumentFactExtractor = null)
    {
        this.questionClassifier = questionClassifier ?? new QuestionClassifier();
        this.officialDocumentFactExtractor = officialDocumentFactExtractor ?? new OfficialDocumentFactExtractor();
    }

    public FactResolutionResult Resolve(
        string productName,
        string aiIndexFolder,
        string inquiryText,
        InquiryFocus? inquiryFocus = null)
    {
        var classification = questionClassifier.Classify(inquiryText, inquiryFocus);
        var curatedCatalog = CuratedFactCatalogStore.Load(aiIndexFolder, productName);
        var builtInCuratedCatalog = curatedCatalog is null
            ? CuratedFactCatalogStore.GetBuiltInDefault(productName, inquiryText)
            : null;
        var curatedFacts = CuratedFactCatalogStore.ToResolvedFacts(curatedCatalog);
        var builtInCuratedFacts = CuratedFactCatalogStore.ToResolvedFacts(builtInCuratedCatalog);
        var effectiveCuratedFacts = curatedFacts.Count > 0 ? curatedFacts : builtInCuratedFacts;
        var candidateFacts = LoadCandidateFacts(productName, aiIndexFolder);
        var resolvedFacts = ResolveRequestedFacts(classification, candidateFacts, curatedFacts, builtInCuratedFacts);
        var missingFacts = resolvedFacts
            .Where(static fact => string.Equals(fact.Status, FactStatuses.Missing, StringComparison.OrdinalIgnoreCase))
            .Select(static fact => fact.Key)
            .ToList();
        var conflicts = resolvedFacts
            .Where(static fact => string.Equals(fact.Status, FactStatuses.Conflict, StringComparison.OrdinalIgnoreCase))
            .Select(static fact => fact.Key)
            .ToList();
        var crawlerConflicts = BuildCrawlerConflicts(effectiveCuratedFacts, candidateFacts);

        return new FactResolutionResult
        {
            Classification = classification,
            CandidateFacts = candidateFacts,
            ResolvedFacts = resolvedFacts,
            AnswerReadiness = DetermineAnswerReadiness(classification, resolvedFacts, missingFacts, conflicts),
            MissingFacts = missingFacts,
            Conflicts = conflicts,
            CrawlerConflicts = crawlerConflicts,
            LlmPromptUsesResolvedFacts = resolvedFacts.Count > 0,
        };
    }

    private IReadOnlyList<CandidateFact> LoadCandidateFacts(string productName, string aiIndexFolder)
    {
        var catalog = FactCatalogStore.Load(aiIndexFolder, productName);
        if (catalog is not null && catalog.CandidateFacts.Count > 0)
        {
            return catalog.CandidateFacts;
        }

        var officialDocument = LoadOfficialIndex(aiIndexFolder, productName);
        return officialDocument is null
            ? []
            : officialDocumentFactExtractor.Extract(officialDocument);
    }

    private static AiOfficialDocumentIndexDocument? LoadOfficialIndex(string aiIndexFolder, string productName)
    {
        if (string.IsNullOrWhiteSpace(aiIndexFolder) || string.IsNullOrWhiteSpace(productName))
        {
            return null;
        }

        var productIndexFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
        var filePath = Path.Combine(productIndexFolder, AiOfficialDocumentIndexBuilder.IndexFileName);
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            return JsonSerializer.Deserialize<AiOfficialDocumentIndexDocument>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<ResolvedFact> ResolveRequestedFacts(
        QuestionClassificationResult classification,
        IReadOnlyList<CandidateFact> candidateFacts,
        IReadOnlyList<ResolvedFact> curatedFacts,
        IReadOnlyList<ResolvedFact> builtInCuratedFacts)
    {
        var resolved = new List<ResolvedFact>();
        foreach (var requestedFact in classification.RequestedFacts)
        {
            if (string.Equals(requestedFact, FactKeys.CurrentInstalledVersion, StringComparison.OrdinalIgnoreCase))
            {
                resolved.Add(new ResolvedFact
                {
                    Key = FactKeys.CurrentInstalledVersion,
                    Value = classification.CurrentInstalledVersion,
                    Status = string.IsNullOrWhiteSpace(classification.CurrentInstalledVersion)
                        ? FactStatuses.Missing
                        : FactStatuses.Confirmed,
                    Confidence = string.IsNullOrWhiteSpace(classification.CurrentInstalledVersion)
                        ? FactConfidences.Low
                        : FactConfidences.High,
                    SourceType = "Inquiry",
                    Explanation = "問い合わせ本文から現在利用中バージョンとして抽出しました。",
                });
                continue;
            }

            if (string.Equals(requestedFact, FactKeys.UpgradePossibility, StringComparison.OrdinalIgnoreCase))
            {
                resolved.Add(new ResolvedFact
                {
                    Key = FactKeys.UpgradePossibility,
                    Value = "Unknown",
                    Status = FactStatuses.Missing,
                    Confidence = FactConfidences.Low,
                    SourceType = "OfficialDoc",
                    Explanation = "アップグレード可否は専用の公式アップグレードパス根拠が必要です。",
                });
                continue;
            }

            var curated = curatedFacts.FirstOrDefault(fact =>
                string.Equals(fact.Key, requestedFact, StringComparison.OrdinalIgnoreCase));
            if (curated is not null)
            {
                resolved.Add(curated);
                continue;
            }

            var candidates = candidateFacts
                .Where(fact => string.Equals(fact.Key, requestedFact, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var userConfirmedCandidates = candidates
                .Where(static fact => string.Equals(fact.SourceType, "UserConfirmed", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (userConfirmedCandidates.Count > 0)
            {
                resolved.Add(ResolveCandidateGroup(requestedFact, userConfirmedCandidates));
                continue;
            }

            var builtInCurated = builtInCuratedFacts.FirstOrDefault(fact =>
                string.Equals(fact.Key, requestedFact, StringComparison.OrdinalIgnoreCase));
            if (builtInCurated is not null)
            {
                resolved.Add(builtInCurated);
                continue;
            }

            resolved.Add(ResolveCandidateGroup(requestedFact, candidates));
        }

        return resolved
            .GroupBy(static fact => fact.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private static IReadOnlyList<string> BuildCrawlerConflicts(
        IReadOnlyList<ResolvedFact> curatedFacts,
        IReadOnlyList<CandidateFact> candidateFacts)
    {
        if (curatedFacts.Count == 0 || candidateFacts.Count == 0)
        {
            return [];
        }

        var conflicts = new List<string>();
        foreach (var curated in curatedFacts)
        {
            var differentCandidates = candidateFacts
                .Where(candidate =>
                    string.Equals(candidate.Key, curated.Key, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(candidate.Value, curated.Value, StringComparison.OrdinalIgnoreCase))
                .GroupBy(static candidate => candidate.Value, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToList();
            foreach (var candidate in differentCandidates)
            {
                conflicts.Add(
                    $"Crawler detected different candidate: {curated.Key}: {candidate.Value}; Current curated value: {curated.Value}; SourceUrl: {candidate.SourceUrl}; Action: needs review");
            }
        }

        return conflicts;
    }

    private static ResolvedFact ResolveCandidateGroup(
        string requestedFact,
        IReadOnlyList<CandidateFact> candidates)
    {
        if (candidates.Count == 0)
        {
            return new ResolvedFact
            {
                Key = requestedFact,
                Status = FactStatuses.Missing,
                Confidence = FactConfidences.Low,
                SourceType = "OfficialDoc",
                Explanation = "該当するOfficialDoc由来Fact候補がありません。",
            };
        }

        var groupedByValue = candidates
            .GroupBy(static fact => fact.Value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Max(fact => ConfidenceRank(fact.Confidence)))
            .ThenByDescending(static group => VersionOrHotfixRank(group.Key))
            .ToList();
        var bestGroup = groupedByValue[0];
        var best = bestGroup
            .OrderByDescending(static fact => ConfidenceRank(fact.Confidence))
            .First();

        return new ResolvedFact
        {
            Key = requestedFact,
            Value = best.Value,
            Status = groupedByValue.Count == 1 ? FactStatuses.Confirmed : FactStatuses.Conflict,
            Confidence = best.Confidence,
            SourceType = best.SourceType,
            SourceUrls = bestGroup
                .Select(static fact => fact.SourceUrl)
                .Where(static url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Explanation = groupedByValue.Count == 1
                ? "OfficialDoc由来Fact候補から確定しました。"
                : "OfficialDoc由来Fact候補が複数値で競合しています。",
        };
    }

    private static string DetermineAnswerReadiness(
        QuestionClassificationResult classification,
        IReadOnlyList<ResolvedFact> resolvedFacts,
        IReadOnlyList<string> missingFacts,
        IReadOnlyList<string> conflicts)
    {
        if (conflicts.Count > 0)
        {
            return AnswerReadiness.NeedsConfirmation;
        }

        if (classification.QuestionTypes.Contains(QuestionTypes.UpgradePossibilityQuestion, StringComparer.OrdinalIgnoreCase) &&
            missingFacts.Contains(FactKeys.UpgradePossibility, StringComparer.OrdinalIgnoreCase))
        {
            return AnswerReadiness.NeedsConfirmation;
        }

        if (classification.QuestionTypes.Contains(QuestionTypes.LatestVersionQuestion, StringComparer.OrdinalIgnoreCase))
        {
            var latestRequested = classification.RequestedFacts
                .Where(static fact => fact is FactKeys.LatestSastVersion or FactKeys.LatestEnginePackVersion or FactKeys.LatestHotfixVersion)
                .ToList();
            var allLatestConfirmed = latestRequested.Count > 0 &&
                latestRequested.All(requested =>
                    resolvedFacts.Any(fact =>
                        string.Equals(fact.Key, requested, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(fact.Status, FactStatuses.Confirmed, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(fact.Confidence, FactConfidences.High, StringComparison.OrdinalIgnoreCase)));
            return allLatestConfirmed
                ? AnswerReadiness.AutoAnswerable
                : AnswerReadiness.InsufficientEvidence;
        }

        return resolvedFacts.Any(static fact => string.Equals(fact.Status, FactStatuses.Confirmed, StringComparison.OrdinalIgnoreCase))
            ? AnswerReadiness.NeedsConfirmation
            : AnswerReadiness.InsufficientEvidence;
    }

    private static int ConfidenceRank(string confidence)
    {
        return confidence switch
        {
            FactConfidences.High => 3,
            FactConfidences.Medium => 2,
            _ => 1,
        };
    }

    private static int VersionOrHotfixRank(string value)
    {
        var hotfixMatch = System.Text.RegularExpressions.Regex.Match(value, @"HF(?<n>\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (hotfixMatch.Success && int.TryParse(hotfixMatch.Groups["n"].Value, out var hotfix))
        {
            return hotfix;
        }

        var rank = 0;
        foreach (var part in value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var number))
            {
                rank = (rank * 1000) + number;
            }
        }

        return rank;
    }
}
