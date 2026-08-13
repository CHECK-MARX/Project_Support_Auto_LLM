using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Core.Quality;

public static partial class AnswerQualityEvaluator
{
    private static readonly string[] InternalMarkers =
    [
        "[RAG Evidence]", "Evidence score", "Selection reason", "fallback",
        "TopK", "InsufficientEvidence", "CompletedWithFallback", "Phase 14",
        "Phase 15", "Phase 16", "Phase 17", "RAG内部", "内部スコア",
    ];

    private static readonly string[] InsufficientMarkers =
    [
        "根拠不足", "情報不足", "確認できません", "判断できません",
        "断定できません", "insufficient evidence",
    ];

    public static AnswerQualityEvaluationResult Evaluate(
        AnswerQualityEvaluationInput input,
        AnswerQualityThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var rules = thresholds ?? AnswerQualityThresholds.Default;
        var catalog = EnsureCatalog(input.Catalog, input.ProductName);
        var queryProfile = TopicEntityAnalyzer.Extract(input.Question, catalog);
        var answerProfile = TopicEntityAnalyzer.Extract(input.Answer, catalog);
        var topicComparison = TopicEntityAnalyzer.Compare(queryProfile, answerProfile);
        var topicAlignment = CalculateTopicAlignment(queryProfile, topicComparison);

        var answerClaims = TechnicalClaimExtractor.Extract(input.Answer, catalog);
        var supportClaims = TechnicalClaimExtractor.Extract(
            string.Join('\n', new[] { input.Question }.Concat(input.Evidence.Select(static item => item.Text))),
            catalog);
        var supportedKeys = supportClaims
            .Select(static claim => $"{claim.Kind}|{claim.NormalizedValue}")
            .ToHashSet(StringComparer.Ordinal);
        var unsupported = answerClaims
            .Where(claim => !supportedKeys.Contains($"{claim.Kind}|{claim.NormalizedValue}"))
            .Select(static claim => new UnsupportedTechnicalClaim
            {
                Kind = claim.Kind,
                Value = claim.Value,
                IsMajor = claim.IsMajor,
            })
            .ToList();

        var grounding = CalculateGrounding(input, answerClaims.Count, unsupported.Count, topicAlignment);
        var technicalFidelity = answerClaims.Count == 0
            ? 1.0
            : Clamp(1.0 - (unsupported.Count / (double)answerClaims.Count));
        var legacyRequiredCoverage = RequiredCoverage(queryProfile, input.Question);
        var legacyObservedCoverage = ObservedCoverage(input.Answer, answerClaims);
        var phase175RequiredCoverage = input.RequiredCoverage.Count > 0
            ? input.RequiredCoverage.Distinct(StringComparer.Ordinal).ToList()
            : CoverageAnalyzer.Required(input.Question, queryProfile);
        var evidenceObservedCoverage = CoverageAnalyzer.Observe(input.Evidence);
        var answerObservedCoverage = CoverageAnalyzer.Observe(input.Answer);
        var missingEvidenceCoverage = phase175RequiredCoverage
            .Where(item => !evidenceObservedCoverage.Contains(item))
            .ToList();
        var missingAnswerCoverage = phase175RequiredCoverage
            .Where(item => !answerObservedCoverage.Contains(item))
            .ToList();
        var evidenceCoverage = phase175RequiredCoverage.Count == 0
            ? 1.0
            : (phase175RequiredCoverage.Count - missingEvidenceCoverage.Count) / (double)phase175RequiredCoverage.Count;
        var answerCoverage = phase175RequiredCoverage.Count == 0
            ? 1.0
            : (phase175RequiredCoverage.Count - missingAnswerCoverage.Count) / (double)phase175RequiredCoverage.Count;
        var coverage = input.UseSeparatedCoverage
            ? answerCoverage
            : legacyRequiredCoverage.Count == 0
                ? 1.0
                : legacyRequiredCoverage.Count(item => legacyObservedCoverage.Contains(item)) / (double)legacyRequiredCoverage.Count;
        var observedCoverage = input.UseSeparatedCoverage ? answerObservedCoverage : legacyObservedCoverage;
        var directness = CalculateDirectness(input.Answer, topicAlignment, coverage);
        var actionability = CalculateActionability(queryProfile, input.Answer, observedCoverage);
        var leakageCount = InternalMarkers.Count(marker =>
            input.Answer.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var customerReadiness = CalculateCustomerReadiness(input.Answer, leakageCount);
        var conflictCount = CountConflicts(input, queryProfile, catalog);

        var blocking = new List<string>();
        var warnings = new List<string>();
        if (topicComparison.TopicConflict)
        {
            blocking.Add("TopicConflict");
        }
        if (unsupported.Any(static item => item.IsMajor))
        {
            blocking.Add("MajorUnsupportedTechnicalClaim");
        }
        if (leakageCount > 0)
        {
            warnings.Add("InternalLeakage");
        }
        if (conflictCount > 0)
        {
            warnings.Add("EvidenceConflict");
        }
        if (unsupported.Count > 0)
        {
            warnings.Add("UnsupportedTechnicalClaim");
        }
        if (input.UseSeparatedCoverage && missingEvidenceCoverage.Count > 0)
        {
            warnings.Add("MissingEvidenceCoverage");
        }
        if (input.UseSeparatedCoverage && missingAnswerCoverage.Count > 0)
        {
            warnings.Add("MissingAnswerCoverage");
        }

        var decision = Decide(
            input,
            rules,
            directness,
            grounding,
            topicAlignment,
            coverage,
            technicalFidelity,
            actionability,
            customerReadiness,
            leakageCount,
            conflictCount,
            blocking,
            missingEvidenceCoverage,
            missingAnswerCoverage);

        return new AnswerQualityEvaluationResult
        {
            Directness = Round(directness),
            Grounding = Round(grounding),
            TopicAlignment = Round(topicAlignment),
            Coverage = Round(coverage),
            EvidenceCoverage = input.UseSeparatedCoverage ? Round(evidenceCoverage) : null,
            AnswerCoverage = input.UseSeparatedCoverage ? Round(answerCoverage) : null,
            RequiredCoverage = input.UseSeparatedCoverage ? phase175RequiredCoverage : null,
            MissingEvidenceCoverage = input.UseSeparatedCoverage ? missingEvidenceCoverage : null,
            MissingAnswerCoverage = input.UseSeparatedCoverage ? missingAnswerCoverage : null,
            TechnicalFidelity = Round(technicalFidelity),
            UnsupportedClaimCount = unsupported.Count,
            UnsupportedTechnicalClaims = unsupported,
            ConflictCount = conflictCount,
            Actionability = Round(actionability),
            CustomerReadiness = Round(customerReadiness),
            InternalLeakageCount = leakageCount,
            BlockingReasons = blocking.Distinct(StringComparer.Ordinal).ToList(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
            Decision = decision,
        };
    }

    public static TopicEntityCatalog CreateSupportCatalog(string? productName = null) =>
        SupportTopicCatalog.Create(productName);

    private static string Decide(
        AnswerQualityEvaluationInput input,
        AnswerQualityThresholds rules,
        double directness,
        double grounding,
        double topicAlignment,
        double coverage,
        double technicalFidelity,
        double actionability,
        double customerReadiness,
        int leakageCount,
        int conflictCount,
        IReadOnlyCollection<string> blocking,
        IReadOnlyCollection<string> missingEvidenceCoverage,
        IReadOnlyCollection<string> missingAnswerCoverage)
    {
        if (blocking.Count > 0)
        {
            return AnswerQualityDecisions.Blocked;
        }

        if (input.UseSeparatedCoverage)
        {
            var hardInsufficientReason = input.ExistingInsufficientReasons.Any(static reason => reason is
                "NoRelevantEvidence" or "ProductMismatch" or "VersionMismatch" or
                "TopicConflict" or "ConflictingEvidence");
            if (input.Evidence.Count == 0 || hardInsufficientReason ||
                grounding < rules.InsufficientGrounding || missingEvidenceCoverage.Count > 0)
            {
                return AnswerQualityDecisions.InsufficientEvidence;
            }

            if (missingAnswerCoverage.Count > 0)
            {
                return AnswerQualityDecisions.NeedsReview;
            }
        }
        else if (input.Evidence.Count == 0 ||
                 input.ExistingInsufficientReasons.Count > 0 ||
                 grounding < rules.InsufficientGrounding ||
                 coverage < rules.InsufficientCoverage)
        {
            return AnswerQualityDecisions.InsufficientEvidence;
        }

        if (conflictCount > 0 || leakageCount > 0)
        {
            return AnswerQualityDecisions.NeedsReview;
        }

        var ready = directness >= rules.MinimumDirectness &&
            grounding >= rules.MinimumGrounding &&
            topicAlignment >= rules.MinimumTopicAlignment &&
            coverage >= rules.MinimumCoverage &&
            technicalFidelity >= rules.MinimumTechnicalFidelity &&
            actionability >= rules.MinimumActionability &&
            customerReadiness >= rules.MinimumCustomerReadiness;
        return ready ? AnswerQualityDecisions.CustomerReady : AnswerQualityDecisions.NeedsReview;
    }

    private static double CalculateGrounding(
        AnswerQualityEvaluationInput input,
        int claimCount,
        int unsupportedCount,
        double topicAlignment)
    {
        if (input.Evidence.Count == 0)
        {
            return 0;
        }
        if (claimCount > 0)
        {
            return Clamp(1.0 - (unsupportedCount / (double)claimCount));
        }

        return topicAlignment >= 0.75 ? 1.0 : topicAlignment >= 0.5 ? 0.6 : 0.3;
    }

    private static double CalculateTopicAlignment(
        TopicEntityProfile query,
        TopicConflictAssessment comparison)
    {
        if (comparison.TopicConflict)
        {
            return 0;
        }
        if (query.Features.Count > 0 || query.Components.Count > 0)
        {
            return comparison.HasTopicMatch ? 1.0 : 0.25;
        }
        if (query.Products.Count > 0)
        {
            return comparison.MatchedProducts.Count > 0 ? 1.0 : 0.5;
        }
        return 1.0;
    }

    private static double CalculateDirectness(string answer, double topicAlignment, double coverage)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return 0;
        }
        if (InsufficientMarkers.Any(marker => answer.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return 0.25;
        }

        var score = (topicAlignment * 0.65) + (coverage * 0.35);
        return answer.Trim().Length < 20 ? score * 0.5 : Clamp(score);
    }

    private static double CalculateActionability(
        TopicEntityProfile query,
        string answer,
        IReadOnlySet<string> observed)
    {
        var needsAction = query.Intents.Contains("HowTo", StringComparer.Ordinal) ||
            query.Intents.Contains("Command", StringComparer.Ordinal) ||
            query.Intents.Contains("Configuration", StringComparer.Ordinal);
        if (!needsAction)
        {
            return 1.0;
        }

        if (query.Features.Contains("Stream", StringComparer.Ordinal))
        {
            var streamChecks = new[]
            {
                observed.Contains("Setup"),
                observed.Contains("Association"),
                observed.Contains("Verification"),
            };
            return streamChecks.Count(static value => value) / (double)streamChecks.Length;
        }

        var checks = new[]
        {
            observed.Contains("Command") || StepRegex().IsMatch(answer),
            observed.Contains("Execution") || ContainsAny(answer, "実行", "操作", "run", "execute"),
            observed.Contains("Verification"),
        };
        return checks.Count(static value => value) / (double)checks.Length;
    }

    private static double CalculateCustomerReadiness(string answer, int leakageCount)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return 0;
        }

        var score = 0.0;
        if (JapaneseRegex().IsMatch(answer)) score += 0.35;
        if (answer.Contains('。') || PoliteRegex().IsMatch(answer)) score += 0.25;
        if (answer.Trim().Length >= 60) score += 0.25;
        else if (answer.Trim().Length >= 30) score += 0.15;
        if (leakageCount == 0) score += 0.15;
        return Clamp(score);
    }

    private static int CountConflicts(
        AnswerQualityEvaluationInput input,
        TopicEntityProfile query,
        TopicEntityCatalog catalog)
    {
        var count = input.Evidence.Count(item =>
            TopicEntityAnalyzer.Compare(query, TopicEntityAnalyzer.Extract(item.Text, catalog)).TopicConflict);
        var versions = input.Evidence
            .SelectMany(item => TechnicalClaimExtractor.Extract(item.Text, catalog))
            .Where(static claim => claim.Kind == "Version")
            .Select(static claim => claim.NormalizedValue)
            .Distinct(StringComparer.Ordinal)
            .Count();
        return count + (versions > 1 ? 1 : 0);
    }

    private static IReadOnlySet<string> RequiredCoverage(TopicEntityProfile query, string question)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        var upload = query.Operations.Contains("Upload", StringComparer.Ordinal) ||
            query.Features.Contains("Build upload", StringComparer.Ordinal);
        if (upload)
        {
            required.UnionWith(["Command", "Option", "Authentication", "Association", "Execution", "Verification"]);
            return required;
        }

        if (query.Features.Contains("Stream", StringComparer.Ordinal))
        {
            if (query.Intents.Contains("Overview", StringComparer.Ordinal)) required.Add("Overview");
            if (query.Intents.Any(intent => intent is "HowTo" or "Configuration"))
            {
                required.UnionWith(["Setup", "Association", "Verification"]);
            }
            return required;
        }

        if (query.Intents.Any(intent => intent is "HowTo" or "Configuration" or "Command"))
        {
            required.Add("Execution");
        }
        return required;
    }

    private static IReadOnlySet<string> ObservedCoverage(
        string answer,
        IReadOnlyList<AnswerTechnicalClaim> claims)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        if (claims.Any(static claim => claim.Kind == "Command")) observed.Add("Command");
        if (claims.Any(static claim => claim.Kind == "Option")) observed.Add("Option");
        if (ContainsAny(answer, "概要", "とは", "overview", "definition")) observed.Add("Overview");
        if (ContainsAny(answer, "設定", "作成", "手順", "setup", "configure", "create")) observed.Add("Setup");
        if (ContainsAny(answer, "認証", "ログイン", "token", "credential", "authentication")) observed.Add("Authentication");
        if (ContainsAny(answer, "接続", "関連付け", "紐付け", "project", "associate", "connection")) observed.Add("Association");
        if (ContainsAny(answer, "実行", "アップロード", "run", "execute", "upload")) observed.Add("Execution");
        if (ContainsAny(answer, "確認", "検証", "表示", "verify", "confirm", "portal")) observed.Add("Verification");
        return observed;
    }

    private static TopicEntityCatalog EnsureCatalog(TopicEntityCatalog catalog, string? productName)
    {
        var products = catalog.Products.Count > 0
            ? catalog.Products
            : string.IsNullOrWhiteSpace(productName)
                ? []
                : [new TopicAliasDefinition { CanonicalName = productName }];
        return catalog with
        {
            Products = products,
            Components = catalog.Components.Count > 0
                ? catalog.Components
                : [new TopicAliasDefinition { CanonicalName = "Validate", Aliases = ["Perforce Validate"] }],
            Features = catalog.Features.Count > 0
                ? catalog.Features
                :
                [
                    new TopicAliasDefinition { CanonicalName = "Stream", Aliases = ["ストリーム"] },
                    new TopicAliasDefinition { CanonicalName = "License", Aliases = ["ライセンス"] },
                    new TopicAliasDefinition { CanonicalName = "IDE Plugin", Aliases = ["IDEプラグイン", "Eclipse Plugin"] },
                    new TopicAliasDefinition { CanonicalName = "Build upload", Aliases = ["validate build", "build upload", "解析結果をアップロード"] },
                ],
            Objects = catalog.Objects.Count > 0
                ? catalog.Objects
                : [new TopicAliasDefinition { CanonicalName = "Analysis result", Aliases = ["解析結果"] }],
            Entities = catalog.Entities.Count > 0
                ? catalog.Entities
                :
                [
                    new TopicEntityAliasDefinition
                    {
                        Kind = TopicEntityKind.Command,
                        CanonicalValue = "qacli validate build",
                        Aliases = ["validate build"],
                    },
                ],
        };
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);
    private static double Round(double value) => Math.Round(Clamp(value), 6, MidpointRounding.AwayFromZero);

    [GeneratedRegex(@"(?m)^\s*(?:\d+[.)、]|[-*・])")]
    private static partial Regex StepRegex();

    [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]")]
    private static partial Regex JapaneseRegex();

    [GeneratedRegex(@"(?:です|ます|ください|いたします)[。\s]", RegexOptions.IgnoreCase)]
    private static partial Regex PoliteRegex();
}
