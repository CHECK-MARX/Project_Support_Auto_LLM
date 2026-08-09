using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Ranking;

public static partial class TopicEntityRanker
{
    public const string OverviewCoverage = "A:FeatureOverview";
    public const string PurposeCoverage = "B:FeaturePurpose";
    public const string SetupCoverage = "C:SetupOrCreation";
    public const string AssociationCoverage = "D:ProductAssociation";
    public const string VerificationCoverage = "E:Verification";
    public const string UploadCommandCoverage = "U-A:UploadCommand";
    public const string CommandOptionsCoverage = "U-B:CommandOptions";
    public const string AuthenticationCoverage = "U-C:Authentication";
    public const string ProjectAssociationCoverage = "U-D:ProjectAssociation";
    public const string ExecutionCoverage = "U-E:Execution";
    public const string ValidateVerificationCoverage = "U-F:ValidateVerification";
    public const string FailureChecksCoverage = "U-G:FailureChecks";

    public static TopicEntityRankingResult Rank(TopicEntityRankingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var candidateByIndex = request.Candidates.ToDictionary(static candidate => candidate.CandidateIndex);
        var assessed = request.Candidates.Select(candidate => Assess(request, candidate)).ToList();
        var eligible = assessed
            .Where(item => candidateByIndex[item.CandidateIndex].IsManuallySelected ||
                (item.ProductMatch is not false && !item.TopicConflict))
            .ToList();
        var selected = new List<TopicEntityRankingAssessment>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var coverage = new HashSet<string>(StringComparer.Ordinal);

        while (eligible.Count > 0 && selected.Count < Math.Clamp(request.MaxItems, 0, 5))
        {
            var best = eligible
                .Where(item => !fingerprints.Contains(item.TextFingerprint))
                .OrderByDescending(item => item.FinalScore + (0.06 * item.Coverage.Except(coverage).Count()))
                .ThenByDescending(static item => item.TopicScore)
                .ThenByDescending(static item => item.EntityScore)
                .ThenByDescending(static item => item.BaseSearchScore)
                .ThenBy(item => candidateByIndex[item.CandidateIndex].OriginalRank)
                .ThenBy(static item => item.CandidateId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (best is null)
            {
                break;
            }

            selected.Add(best);
            eligible.Remove(best);
            fingerprints.Add(best.TextFingerprint);
            coverage.UnionWith(best.Coverage);
        }

        return new TopicEntityRankingResult
        {
            Selected = selected,
            Assessed = assessed,
            FinalCoverage = coverage,
            InsufficientReasons = BuildInsufficientReasons(request, assessed, selected, coverage, candidateByIndex),
        };
    }

    private static TopicEntityRankingAssessment Assess(
        TopicEntityRankingRequest request,
        TopicEntityRankingCandidate candidate)
    {
        var comparison = TopicEntityAnalyzer.Compare(request.QueryProfile, candidate.Profile);
        var productMatch = ProductMatch(request.RequestedProduct, candidate.ProductName);
        var productScore = productMatch is true ? 1.0 : 0.0;
        var componentScore = MatchScore(request.QueryProfile.Components, candidate.Profile.Components);
        var featureScore = MatchScore(request.QueryProfile.Features, candidate.Profile.Features);
        var operationScore = MatchScore(request.QueryProfile.Operations, candidate.Profile.Operations);
        var intentScore = MatchScore(request.QueryProfile.Intents, candidate.Profile.Intents);
        var queryEntities = request.QueryProfile.Entities
            .Where(static entity => entity.Kind is not TopicEntityKind.Product and not TopicEntityKind.Feature)
            .ToList();
        var entityScore = EntityScore(queryEntities, comparison.MatchedEntities);
        var exactTokens = request.TechnicalTokens
            .Where(token => candidate.Text.Contains(token, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var technicalScore = request.TechnicalTokens.Count == 0
            ? 0
            : (double)exactTokens.Count / request.TechnicalTokens.Count;
        var versionMatch = VersionStatus(request.RequestedVersion, candidate.Version, candidate.Text);
        var versionScore = versionMatch switch
        {
            "exact" => 1.0,
            "near" => 0.5,
            _ => 0,
        };
        var trust = SourceTrust(candidate.SourceType);
        var topicScore = WeightedAverage(
            (0.24, featureScore, request.QueryProfile.Features.Count > 0),
            (0.16, componentScore, request.QueryProfile.Components.Count > 0),
            (0.11, productScore, !string.IsNullOrWhiteSpace(request.RequestedProduct)),
            (0.10, operationScore, request.QueryProfile.Operations.Count > 0));
        var weighted = new List<(double Weight, double Score, bool Applies)>
        {
            (0.24, featureScore, request.QueryProfile.Features.Count > 0),
            (0.16, componentScore, request.QueryProfile.Components.Count > 0),
            (0.11, productScore, !string.IsNullOrWhiteSpace(request.RequestedProduct)),
            (0.10, entityScore, queryEntities.Count > 0),
            (0.10, operationScore, request.QueryProfile.Operations.Count > 0),
            (0.09, intentScore, request.QueryProfile.Intents.Count > 0),
            (0.08, technicalScore, request.TechnicalTokens.Count > 0),
            (0.02, trust, true),
            (0.02, versionScore, !string.IsNullOrWhiteSpace(request.RequestedVersion)),
        };
        AddSearchScores(weighted, candidate);
        var score = WeightedAverage(weighted.ToArray());
        var conflictPenalty = ConflictPenalty(comparison, productMatch, versionMatch);
        score = Math.Clamp(score + conflictPenalty, 0, 1);
        var coverage = BuildCoverage(request.QueryProfile, candidate.Profile, comparison, candidate.Text);

        return new TopicEntityRankingAssessment
        {
            CandidateIndex = candidate.CandidateIndex,
            CandidateId = candidate.CandidateId,
            FinalScore = score,
            TopicScore = topicScore,
            ProductScore = productScore,
            ComponentScore = componentScore,
            FeatureScore = featureScore,
            OperationScore = operationScore,
            IntentScore = intentScore,
            EntityScore = entityScore,
            TechnicalTokenScore = technicalScore,
            BaseSearchScore = Math.Clamp(candidate.BaseSearchScore, 0, 1),
            LexicalScore = candidate.LexicalScore,
            SemanticScore = candidate.SemanticScore,
            SourceTrustScore = trust,
            VersionScore = versionScore,
            ConflictPenalty = conflictPenalty,
            ProductMatch = productMatch,
            VersionMatch = versionMatch,
            TopicConflict = comparison.TopicConflict,
            HasTopicMatch = comparison.HasTopicMatch,
            ConflictKinds = comparison.ConflictKinds,
            Coverage = coverage,
            ExactTechnicalTokens = exactTokens,
            TextFingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(TopicEntityAnalyzer.NormalizeText(candidate.Text)))),
            SelectionReason = BuildSelectionReason(comparison, coverage, conflictPenalty),
        };
    }

    private static void AddSearchScores(
        List<(double Weight, double Score, bool Applies)> weighted,
        TopicEntityRankingCandidate candidate)
    {
        if (candidate.LexicalScore.HasValue || candidate.SemanticScore.HasValue)
        {
            weighted.Add((0.05, Math.Clamp(candidate.LexicalScore ?? 0, 0, 1), candidate.LexicalScore.HasValue));
            weighted.Add((0.03, Math.Clamp(candidate.SemanticScore ?? 0, 0, 1), candidate.SemanticScore.HasValue));
            return;
        }

        weighted.Add((0.08, Math.Clamp(candidate.BaseSearchScore, 0, 1), true));
    }

    private static double ConflictPenalty(
        TopicConflictAssessment comparison,
        bool? productMatch,
        string versionMatch)
    {
        var penalty = 0.0;
        if (productMatch is false)
        {
            penalty -= 0.65;
        }

        if (comparison.ConflictKinds.Contains("Feature", StringComparer.Ordinal))
        {
            penalty -= 0.55;
        }
        else if (comparison.ConflictKinds.Contains("Component", StringComparer.Ordinal))
        {
            penalty -= 0.30;
        }
        else if (comparison.NoTopicMatch)
        {
            penalty -= 0.20;
        }

        if (versionMatch == "mismatch")
        {
            penalty -= 0.18;
        }

        return penalty;
    }

    private static IReadOnlySet<string> BuildCoverage(
        TopicEntityProfile query,
        TopicEntityProfile evidence,
        TopicConflictAssessment comparison,
        string text)
    {
        var coverage = new HashSet<string>(StringComparer.Ordinal);
        if (query.Features.Count > 0 && evidence.Features.Count > 0 && comparison.HasTopicMatch)
        {
            if (ContainsAny(text, "概要", "とは", "overview", "what is", "definition")) coverage.Add(OverviewCoverage);
            if (ContainsAny(text, "用途", "目的", "使用する", "used for", "purpose", "use case")) coverage.Add(PurposeCoverage);
            if (ContainsAny(text, "設定", "作成", "手順", "configure", "configuration", "setup", "create", "step")) coverage.Add(SetupCoverage);
            if (ContainsAny(text, "関連付け", "紐付け", "associate", "association", "link", "project", "qac")) coverage.Add(AssociationCoverage);
            if (ContainsAny(text, "確認", "検証", "状態", "verify", "verification", "confirm", "status", "check")) coverage.Add(VerificationCoverage);
        }

        var uploadWorkflow = query.Features.Any(feature =>
                TopicEntityAnalyzer.NormalizeText(feature) == "build upload") ||
            (query.Operations.Contains("Upload", StringComparer.Ordinal) &&
                query.Components.Any(component => TopicEntityAnalyzer.NormalizeText(component) == "validate"));
        if (uploadWorkflow && !comparison.TopicConflict)
        {
            var hasCommand = ContainsAny(text, "qacli", "command", "コマンド");
            if (hasCommand && ContainsAny(text, "upload", "アップロード", "validate build")) coverage.Add(UploadCommandCoverage);
            if (OptionRegex().IsMatch(text) || ContainsAny(text, "option", "parameter", "オプション", "引数")) coverage.Add(CommandOptionsCoverage);
            if (ContainsAny(text, "auth", "authentication", "login", "credential", "token", "認証", "ログイン")) coverage.Add(AuthenticationCoverage);
            if (ContainsAny(text, "project", "associate", "association", "関連付け", "紐付け")) coverage.Add(ProjectAssociationCoverage);
            if (hasCommand || ContainsAny(text, "execute", "run", "実行")) coverage.Add(ExecutionCoverage);
            if (ContainsAny(text, "validate") && ContainsAny(text, "confirm", "verify", "portal", "確認", "表示")) coverage.Add(ValidateVerificationCoverage);
            if (ContainsAny(text, "fail", "error", "check", "失敗", "エラー", "トラブルシュート")) coverage.Add(FailureChecksCoverage);
        }

        return coverage;
    }

    private static IReadOnlyList<string> BuildInsufficientReasons(
        TopicEntityRankingRequest request,
        IReadOnlyList<TopicEntityRankingAssessment> assessed,
        IReadOnlyList<TopicEntityRankingAssessment> selected,
        IReadOnlySet<string> coverage,
        IReadOnlyDictionary<int, TopicEntityRankingCandidate> candidateByIndex)
    {
        var reasons = new List<string>();
        var hasSpecificTopic = request.QueryProfile.Features.Count > 0 || request.QueryProfile.Components.Count > 0;
        if (hasSpecificTopic && !selected.Any(static item => item.HasTopicMatch)) reasons.Add("NoTopicMatch");
        if (selected.Count == 0 && assessed.Any(static item => item.TopicConflict)) reasons.Add("TopicConflict");
        if (assessed.Count > 0 && assessed.All(static item => item.ProductMatch is false)) reasons.Add("ProductMismatch");

        var featureQuestion = request.QueryProfile.Features.Count > 0;
        if (featureQuestion && request.QueryProfile.Intents.Contains("Overview", StringComparer.Ordinal) &&
            !coverage.Contains(OverviewCoverage)) reasons.Add("MissingOverview");
        if (featureQuestion && request.QueryProfile.Intents.Any(intent => intent is "HowTo" or "Configuration") &&
            !coverage.Contains(SetupCoverage)) reasons.Add("MissingSetupProcedure");
        if (featureQuestion && request.QueryProfile.Intents.Any(intent => intent is "HowTo" or "Configuration") &&
            !coverage.Contains(VerificationCoverage)) reasons.Add("MissingVerification");
        if (featureQuestion && coverage.Count < 3) reasons.Add("LowCoverage");
        var uploadWorkflow = request.QueryProfile.Features.Any(feature =>
                TopicEntityAnalyzer.NormalizeText(feature) == "build upload") ||
            (request.QueryProfile.Operations.Contains("Upload", StringComparer.Ordinal) &&
                request.QueryProfile.Components.Any(component => TopicEntityAnalyzer.NormalizeText(component) == "validate"));
        if (uploadWorkflow && request.QueryProfile.Intents.Contains("Command", StringComparer.Ordinal) &&
            !coverage.Contains(UploadCommandCoverage)) reasons.Add("MissingCommand");
        if (uploadWorkflow && request.QueryProfile.Intents.Contains("HowTo", StringComparer.Ordinal) &&
            (!coverage.Contains(ExecutionCoverage) || !coverage.Contains(ValidateVerificationCoverage)))
        {
            reasons.Add("MissingSetupProcedure");
        }
        if (selected.Count > 0 && selected.Max(static item => item.FinalScore) < 0.25) reasons.Add("LowConfidence");
        if (!string.IsNullOrWhiteSpace(request.RequestedVersion) && selected.Count > 0 &&
            selected.All(static item => item.VersionMatch is not "exact" and not "near")) reasons.Add("VersionMismatch");
        var versions = selected
            .Select(item => candidateByIndex[item.CandidateIndex].Version)
            .Where(static version => !string.IsNullOrWhiteSpace(version))
            .Select(TopicEntityAnalyzer.NormalizeText)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (versions > 1) reasons.Add("ConflictingEvidence");
        return reasons.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string BuildSelectionReason(
        TopicConflictAssessment comparison,
        IReadOnlySet<string> coverage,
        double conflictPenalty)
    {
        if (comparison.TopicConflict)
        {
            return $"Topic conflict: {string.Join(", ", comparison.ConflictKinds)}; penalty={conflictPenalty:0.00}";
        }

        var match = comparison.HasTopicMatch ? "topic matched" : "topic not confirmed";
        return coverage.Count == 0 ? match : $"{match}; coverage={string.Join(",", coverage.Order())}";
    }

    private static double MatchScore(IReadOnlyList<string> query, IReadOnlyList<string> evidence)
    {
        if (query.Count == 0)
        {
            return 0;
        }

        var evidenceValues = evidence.Select(TopicEntityAnalyzer.NormalizeText).ToHashSet(StringComparer.Ordinal);
        return query.Count(value => evidenceValues.Contains(TopicEntityAnalyzer.NormalizeText(value))) /
            (double)query.Count;
    }

    private static double EntityScore(
        IReadOnlyList<TopicEntityValue> query,
        IReadOnlyList<TopicEntityValue> matched)
    {
        if (query.Count == 0)
        {
            return 0;
        }

        var matchedValues = matched
            .Select(entity => (entity.Kind, entity.NormalizedValue))
            .ToHashSet();
        return query.Count(entity => matchedValues.Contains((entity.Kind, entity.NormalizedValue))) /
            (double)query.Count;
    }

    private static double WeightedAverage(params (double Weight, double Score, bool Applies)[] values)
    {
        var applicable = values.Where(static value => value.Applies).ToList();
        var weight = applicable.Sum(static value => value.Weight);
        return weight <= 0
            ? 0
            : applicable.Sum(value => value.Weight * Math.Clamp(value.Score, 0, 1)) / weight;
    }

    private static bool? ProductMatch(string? requested, string? actual)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(actual))
        {
            return null;
        }

        return string.Equals(
            TopicEntityAnalyzer.NormalizeText(requested).Replace(" ", string.Empty, StringComparison.Ordinal),
            TopicEntityAnalyzer.NormalizeText(actual).Replace(" ", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    private static string VersionStatus(string? requested, string? structuredVersion, string text)
    {
        if (string.IsNullOrWhiteSpace(requested)) return "not_requested";
        var expected = ExtractVersion(requested);
        var actual = ExtractVersion(structuredVersion) ?? ExtractVersion(text);
        if (expected is null || actual is null) return "unknown";
        if (expected == actual) return "exact";
        var left = expected.Split('.');
        var right = actual.Split('.');
        return left.Length >= 2 && right.Length >= 2 && left[0] == right[0] && left[1] == right[1]
            ? "near"
            : "mismatch";
    }

    private static string? ExtractVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = VersionRegex().Match(value);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static double SourceTrust(string sourceType)
    {
        var value = TopicEntityAnalyzer.NormalizeText(sourceType)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (value.Contains("officialdoc", StringComparison.Ordinal)) return 1.0;
        if (value.Contains("manual", StringComparison.Ordinal)) return 0.88;
        if (value.Contains("manufacturerreply", StringComparison.Ordinal)) return 0.78;
        if (value.Contains("verifiedpastanswer", StringComparison.Ordinal) || value.Contains("exactpastanswer", StringComparison.Ordinal)) return 0.70;
        if (value.Contains("pastcase", StringComparison.Ordinal) || value.Contains("pastanswer", StringComparison.Ordinal)) return 0.56;
        if (value.Contains("internalnote", StringComparison.Ordinal)) return 0.35;
        return 0.45;
    }

    private static bool ContainsAny(string text, params string[] terms) =>
        terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])v?(\d+(?:\.\d+){1,3})(?![A-Za-z0-9_.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"--[A-Za-z0-9][A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex OptionRegex();
}
