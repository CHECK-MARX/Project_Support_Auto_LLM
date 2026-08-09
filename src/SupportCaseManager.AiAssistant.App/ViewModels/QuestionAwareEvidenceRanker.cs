using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed record class QuestionAwareEvidenceSelectionContext
{
    public bool Enabled { get; init; }

    public string InquiryText { get; init; } = string.Empty;

    public string? ProductName { get; init; }

    public string? TargetVersion { get; init; }

    public string RankingMode { get; init; } = EvidenceRankingModes.Phase15;

    public bool UsePhase175QualityControls { get; init; }

    public bool UseCoverageAwareEvidenceSelection { get; init; }

    public int CoverageAwareMaxEvidenceItems { get; init; } = 5;

    public bool UseRustEvidenceSelector { get; init; }

    public bool EnableRustSelectorShadowMode { get; init; }

    public int RustEvidenceSelectorTimeoutMs { get; init; } = 2000;

    public string RustEvidenceSelectorExecutablePath { get; init; } = string.Empty;

    public int ShadowMinimumRunsForReadiness { get; init; } = 50;

    public int ShadowMaxStoredRecords { get; init; } = 500;

    public string RustShadowObservationFilePath { get; init; } = string.Empty;

    public int MaxPromptChars { get; init; } = 6000;
}

public sealed record class QuestionAwareEvidenceAssessment
{
    public required SearchSourceViewModel Item { get; init; }

    public double FinalScore { get; init; }

    public double QuestionTypeScore { get; init; }

    public double TechnicalTokenScore { get; init; }

    public double SourceTrustScore { get; init; }

    public IReadOnlySet<string> Coverage { get; init; } = new HashSet<string>();

    public IReadOnlyList<string> ExactTechnicalTokens { get; init; } = [];

    public bool? ProductMatch { get; init; }

    public string VersionMatch { get; init; } = "not_requested";

    public string TextFingerprint { get; init; } = string.Empty;

    public double TopicScore { get; init; }

    public double EntityScore { get; init; }

    public double ConflictPenalty { get; init; }

    public double ExclusionPenalty { get; init; }

    public bool ExplicitlyExcluded { get; init; }

    public bool TopicConflict { get; init; }

    public string SelectionReason { get; init; } = string.Empty;
}

public sealed record class QuestionAwareEvidenceRankingResult
{
    public IReadOnlyList<QuestionAwareEvidenceAssessment> Ranked { get; init; } = [];

    public IReadOnlySet<string> FinalCoverage { get; init; } = new HashSet<string>();

    public IReadOnlyList<string> QuestionTypes { get; init; } = [];

    public IReadOnlyList<string> InsufficientReasons { get; init; } = [];

    public string RankingMode { get; init; } = EvidenceRankingModes.Phase15;
}

public static partial class QuestionAwareEvidenceRanker
{
    public const string UploadCommand = "A:UploadCommand";
    public const string CommandOptions = "B:CommandOptions";
    public const string Authentication = "C:Authentication";
    public const string ProjectAssociation = "D:ProjectAssociation";
    public const string Execution = "E:Execution";
    public const string ValidateVerification = "F:ValidateVerification";
    public const string FailureChecks = "G:FailureChecks";

    public static QuestionAwareEvidenceRankingResult Rank(
        IEnumerable<SearchSourceViewModel> candidates,
        QuestionAwareEvidenceSelectionContext context,
        int maxItems)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);
        var classification = new QuestionClassifier().Classify(context.InquiryText);
        var tokens = ExtractExactTechnicalTokens(context.InquiryText);
        var requestedVersion = !string.IsNullOrWhiteSpace(context.TargetVersion)
            ? context.TargetVersion.Trim()
            : ExtractVersion(context.InquiryText);
        var uploadWorkflow = ContainsAny(context.InquiryText, "upload", "アップロード") &&
            ContainsAny(context.InquiryText, "validate");
        var allAssessments = candidates
            .Select(item => Assess(item, context, classification.QuestionTypes, tokens, requestedVersion, uploadWorkflow))
            .ToList();
        var assessed = allAssessments
            .Where(static item => item.Item.IsManuallySelected || item.ProductMatch is not false)
            .ToList();
        var selected = new List<QuestionAwareEvidenceAssessment>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var coverage = new HashSet<string>(StringComparer.Ordinal);
        while (assessed.Count > 0 && selected.Count < Math.Clamp(maxItems, 0, 5))
        {
            var best = assessed
                .Where(item => !fingerprints.Contains(item.TextFingerprint))
                .OrderByDescending(item => item.FinalScore + (0.12 * item.Coverage.Except(coverage).Count()))
                .ThenByDescending(static item => item.QuestionTypeScore)
                .ThenByDescending(static item => item.TechnicalTokenScore)
                .ThenBy(static item => item.Item.SourceId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (best is null)
            {
                break;
            }

            selected.Add(best);
            assessed.Remove(best);
            fingerprints.Add(best.TextFingerprint);
            coverage.UnionWith(best.Coverage);
        }

        var reasons = BuildInsufficientReasons(
            selected,
            coverage,
            classification.QuestionTypes,
            requestedVersion,
            uploadWorkflow,
            allAssessments.Count > 0 && allAssessments.All(static item => item.ProductMatch is false));
        return new QuestionAwareEvidenceRankingResult
        {
            Ranked = selected,
            FinalCoverage = coverage,
            QuestionTypes = classification.QuestionTypes,
            InsufficientReasons = reasons,
            RankingMode = EvidenceRankingModes.Phase15,
        };
    }

    public static IReadOnlyList<string> ExtractExactTechnicalTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return OptionRegex().Matches(query).Select(static match => match.Value)
            .Concat(IdentifierRegex().Matches(query).Select(static match => match.Value))
            .Select(static token => token.Trim('.', ',', ':', ';', '(', ')', '[', ']', '{', '}'))
            .Where(static token => token.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static token => token, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static QuestionAwareEvidenceAssessment Assess(
        SearchSourceViewModel item,
        QuestionAwareEvidenceSelectionContext context,
        IReadOnlyList<string> questionTypes,
        IReadOnlyList<string> tokens,
        string? requestedVersion,
        bool uploadWorkflow)
    {
        var text = string.Join('\n', item.Title, item.Source.QuestionText, item.Source.InternalMemo, item.Text);
        var exact = tokens.Where(token => text.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
        var technicalScore = tokens.Count == 0 ? 0 : (double)exact.Count / tokens.Count;
        var questionScore = QuestionTypeScore(text, questionTypes);
        var coverage = BuildCoverage(text, uploadWorkflow);
        var coverageScore = uploadWorkflow ? coverage.Count / 7.0 : 0;
        var trust = SourceTrust(item.SourceType);
        bool? productMatch = string.IsNullOrWhiteSpace(context.ProductName) || string.IsNullOrWhiteSpace(item.ProductName)
            ? null
            : string.Equals(Normalize(context.ProductName), Normalize(item.ProductName), StringComparison.Ordinal);
        var versionMatch = VersionStatus(requestedVersion, text);
        var baseScore = Math.Clamp(item.Score ?? 0, 0, 1);
        var finalScore = (0.43 * baseScore) + (0.24 * questionScore) +
            (0.16 * technicalScore) + (0.11 * coverageScore) + (0.06 * trust);
        if (productMatch is false)
        {
            finalScore -= 0.45;
        }

        finalScore += versionMatch switch
        {
            "exact" => 0.04,
            "mismatch" => -0.12,
            _ => 0,
        };
        return new QuestionAwareEvidenceAssessment
        {
            Item = item,
            FinalScore = Math.Clamp(finalScore, 0, 1),
            QuestionTypeScore = questionScore,
            TechnicalTokenScore = technicalScore,
            SourceTrustScore = trust,
            Coverage = coverage,
            ExactTechnicalTokens = exact,
            ProductMatch = productMatch,
            VersionMatch = versionMatch,
            TextFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(item.Text)))),
        };
    }

    private static HashSet<string> BuildCoverage(string text, bool uploadWorkflow)
    {
        var coverage = new HashSet<string>(StringComparer.Ordinal);
        if (!uploadWorkflow)
        {
            return coverage;
        }

        var hasCommand = ContainsAny(text, "qacli", "command", "コマンド") || CodeBlockRegex().IsMatch(text);
        if (hasCommand && ContainsAny(text, "upload", "アップロード", "validate")) coverage.Add(UploadCommand);
        if (OptionRegex().IsMatch(text) || ContainsAny(text, "option", "オプション", "parameter", "引数")) coverage.Add(CommandOptions);
        if (ContainsAny(text, "auth", "authentication", "login", "credential", "token", "認証", "ログイン")) coverage.Add(Authentication);
        if (ContainsAny(text, "project", "プロジェクト", "associate", "関連付け")) coverage.Add(ProjectAssociation);
        if (hasCommand || ContainsAny(text, "execute", "run", "実行")) coverage.Add(Execution);
        if (ContainsAny(text, "validate") && ContainsAny(text, "confirm", "verify", "portal", "確認", "表示")) coverage.Add(ValidateVerification);
        if (ContainsAny(text, "fail", "error", "check", "失敗", "エラー", "確認事項")) coverage.Add(FailureChecks);
        return coverage;
    }

    private static double QuestionTypeScore(string text, IReadOnlyList<string> questionTypes)
    {
        var scores = new List<double>();
        if (questionTypes.Contains(QuestionTypes.CommandQuestion, StringComparer.OrdinalIgnoreCase))
        {
            var matches = new[]
            {
                CodeBlockRegex().IsMatch(text), OptionRegex().IsMatch(text),
                ContainsAny(text, "qacli", "cli", "command", "コマンド"),
                ContainsAny(text, "parameter", "option", "引数", "オプション"),
            };
            scores.Add(matches.Count(static value => value) / (double)matches.Length);
        }

        if (questionTypes.Contains(QuestionTypes.HowToQuestion, StringComparer.OrdinalIgnoreCase))
        {
            var matches = new[]
            {
                NumberedStepRegex().IsMatch(text), ContainsAny(text, "prerequisite", "事前", "前提"),
                ContainsAny(text, "auth", "login", "認証", "接続"), ContainsAny(text, "execute", "run", "実行"),
                ContainsAny(text, "success", "complete", "完了", "成功"), ContainsAny(text, "verify", "confirm", "確認"),
            };
            scores.Add(matches.Count(static value => value) / (double)matches.Length);
        }

        AddBinaryScore(scores, questionTypes, QuestionTypes.ConfigurationQuestion, text, "設定", "config", "option");
        AddBinaryScore(scores, questionTypes, QuestionTypes.TroubleshootingQuestion, text, "原因", "対処", "error", "失敗");
        AddBinaryScore(scores, questionTypes, QuestionTypes.VersionQuestion, text, "version", "バージョン", "release");
        AddBinaryScore(scores, questionTypes, QuestionTypes.PermissionQuestion, text, "permission", "権限", "role", "access");
        AddBinaryScore(scores, questionTypes, QuestionTypes.ErrorMessageQuestion, text, "error message", "error code", "エラーメッセージ", "エラーコード");
        return scores.Count == 0 ? 0 : scores.Max();
    }

    private static void AddBinaryScore(List<double> scores, IReadOnlyList<string> questionTypes, string type, string text, params string[] terms)
    {
        if (questionTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            scores.Add(ContainsAny(text, terms) ? 1 : 0);
        }
    }

    private static IReadOnlyList<string> BuildInsufficientReasons(
        IReadOnlyList<QuestionAwareEvidenceAssessment> selected,
        IReadOnlySet<string> coverage,
        IReadOnlyList<string> types,
        string? requestedVersion,
        bool uploadWorkflow,
        bool allCandidatesMismatchProduct)
    {
        var reasons = new List<string>();
        if (selected.Count == 0)
        {
            reasons.Add("NoRelevantEvidence");
            if (allCandidatesMismatchProduct) reasons.Add("ProductMismatch");
        }

        if (uploadWorkflow && types.Contains(QuestionTypes.CommandQuestion) && !coverage.Contains(UploadCommand)) reasons.Add("MissingCommand");
        if (uploadWorkflow && types.Contains(QuestionTypes.HowToQuestion) && (!coverage.Contains(Execution) || !coverage.Contains(ValidateVerification))) reasons.Add("MissingProcedure");
        if (!string.IsNullOrWhiteSpace(requestedVersion) && !selected.Any(item => item.VersionMatch is "exact" or "near")) reasons.Add("MissingVersionSpecificEvidence");
        if (uploadWorkflow && coverage.Count < 4) reasons.Add("LowCoverage");
        if (selected.Count > 0 && selected.Max(static item => item.FinalScore) < 0.25) reasons.Add("LowConfidence");
        return reasons.Distinct(StringComparer.Ordinal).ToList();
    }

    private static double SourceTrust(string sourceType)
    {
        var value = Normalize(sourceType).Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        if (value.Contains("officialdoc", StringComparison.Ordinal)) return 1.0;
        if (value.Contains("manual", StringComparison.Ordinal)) return 0.88;
        if (value.Contains("manufacturerreply", StringComparison.Ordinal)) return 0.78;
        if (value.Contains("verifiedpastanswer", StringComparison.Ordinal) || value.Contains("exactpastanswer", StringComparison.Ordinal)) return 0.70;
        if (value.Contains("pastcase", StringComparison.Ordinal) || value.Contains("pastanswer", StringComparison.Ordinal)) return 0.56;
        if (value.Contains("internalnote", StringComparison.Ordinal)) return 0.35;
        return 0.45;
    }

    private static string VersionStatus(string? requestedVersion, string text)
    {
        if (string.IsNullOrWhiteSpace(requestedVersion)) return "not_requested";
        var requested = ExtractVersion(requestedVersion);
        var actual = ExtractVersion(text);
        if (requested is null || actual is null) return "unknown";
        if (string.Equals(requested, actual, StringComparison.OrdinalIgnoreCase)) return "exact";
        var requestedParts = requested.Split('.');
        var actualParts = actual.Split('.');
        return requestedParts.Length >= 2 && actualParts.Length >= 2 && requestedParts[0] == actualParts[0] && requestedParts[1] == actualParts[1]
            ? "near"
            : "mismatch";
    }

    private static string? ExtractVersion(string text) => VersionRegex().Match(text) is { Success: true } match ? match.Groups[1].Value : null;

    private static bool ContainsAny(string? text, params string[] terms) => terms.Any(term => (text ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string? text) => (text ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim().ToLower(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"--[A-Za-z0-9][A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex OptionRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])[A-Za-z][A-Za-z0-9_.+#/-]{2,}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])v?(\d+(?:\.\d+){1,3})(?![A-Za-z0-9_.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"```|(?:^|\n)\s*(?:\$|>|C:\\|qacli\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"(?:^|\n)\s*(?:\d+[.)]|step\s+\d+|手順\s*\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedStepRegex();
}
