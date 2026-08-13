using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Codex;

public enum CodexAbAnswerability
{
    NoAnswer,
    Answerable,
    InsufficientEvidence,
}

public sealed record CodexAbAnswerSample
{
    public string Variant { get; init; } = string.Empty;
    public string ComparisonKey { get; init; } = string.Empty;
    public string AnswerText { get; init; } = string.Empty;
    public TimeSpan GenerationDuration { get; init; }
    public IReadOnlyList<string> ExistingEvidenceSourceTypes { get; init; } = [];
    public IReadOnlyList<RagLabEvidenceItem> RagLabEvidence { get; init; } = [];
}

public sealed record CodexAbVariantMetrics
{
    public string Variant { get; init; } = string.Empty;
    public CodexAbAnswerability Answerability { get; init; }
    public int UsedEvidenceCount { get; init; }
    public int OfficialCount { get; init; }
    public int ManualCount { get; init; }
    public int PastCaseCount { get; init; }
    public int OtherEvidenceCount { get; init; }
    public int AnswerLength { get; init; }
    public int ConfirmationCount { get; init; }
    public int ProductMismatchCount { get; init; }
    public int VersionMismatchCount { get; init; }
    public int EvidenceConflictCount { get; init; }
    public int UnverifiedEvidenceFieldCount { get; init; }
    public bool ContainsInternalRagTerms { get; init; }
    public bool HasConcreteSteps { get; init; }
    public bool HasJapaneseText { get; init; }
    public long GenerationMilliseconds { get; init; }
}

public sealed record CodexEvidenceAbComparisonResult
{
    public required CodexAbVariantMetrics Baseline { get; init; }
    public required CodexAbVariantMetrics WithEvidence { get; init; }
    public required CodexTechnicalValueDiff TechnicalValueDiff { get; init; }
    public string QualityDecision { get; init; } = "自動判定しません。各確認項目を手動で評価してください。";
}

public interface ICodexEvidenceAbComparisonService
{
    CodexEvidenceAbComparisonResult Compare(
        CodexAbAnswerSample baseline,
        CodexAbAnswerSample withEvidence,
        IEnumerable<string>? productNames = null);
}

public sealed partial class CodexEvidenceAbComparisonService : ICodexEvidenceAbComparisonService
{
    private static readonly string[] InsufficientEvidenceMarkers =
    [
        "根拠不足",
        "情報不足",
        "確認できません",
        "判断できません",
        "断定できません",
        "insufficient evidence",
    ];

    private static readonly string[] InternalRagMarkers =
    [
        "[RAG Evidence]",
        "[End RAG Evidence]",
        "Selection reason:",
        "Product match:",
        "Version match:",
    ];

    private readonly ICodexTechnicalValueDiffDetector diffDetector;

    public CodexEvidenceAbComparisonService(ICodexTechnicalValueDiffDetector diffDetector)
    {
        this.diffDetector = diffDetector;
    }

    public CodexEvidenceAbComparisonResult Compare(
        CodexAbAnswerSample baseline,
        CodexAbAnswerSample withEvidence,
        IEnumerable<string>? productNames = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(withEvidence);
        if (string.IsNullOrWhiteSpace(baseline.ComparisonKey)
            || !string.Equals(baseline.ComparisonKey, withEvidence.ComparisonKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A/B比較は同一の製品・問い合わせで記録した回答に限られます。");
        }

        return new CodexEvidenceAbComparisonResult
        {
            Baseline = BuildMetrics(baseline),
            WithEvidence = BuildMetrics(withEvidence),
            TechnicalValueDiff = diffDetector.Compare(baseline.AnswerText, withEvidence.AnswerText, productNames),
        };
    }

    public static string CreateComparisonKey(string? productName, string? inquiryText)
    {
        var normalized = $"{NormalizeForKey(productName)}\n{NormalizeForKey(inquiryText)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static CodexAbVariantMetrics BuildMetrics(CodexAbAnswerSample sample)
    {
        var sourceTypes = sample.ExistingEvidenceSourceTypes
            .Concat(sample.RagLabEvidence.Select(static item => item.SourceType ?? string.Empty))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var officialCount = sourceTypes.Count(IsOfficial);
        var manualCount = sourceTypes.Count(IsManual);
        var pastCaseCount = sourceTypes.Count(IsPastCase);
        var answer = sample.AnswerText ?? string.Empty;

        return new CodexAbVariantMetrics
        {
            Variant = sample.Variant,
            Answerability = DetermineAnswerability(answer),
            UsedEvidenceCount = sourceTypes.Length,
            OfficialCount = officialCount,
            ManualCount = manualCount,
            PastCaseCount = pastCaseCount,
            OtherEvidenceCount = Math.Max(0, sourceTypes.Length - officialCount - manualCount - pastCaseCount),
            AnswerLength = answer.Length,
            ConfirmationCount = CountConfirmationLines(answer),
            ProductMismatchCount = sample.RagLabEvidence.Count(static item => item.ProductMatch == false),
            VersionMismatchCount = sample.RagLabEvidence.Count(static item => item.VersionMatch == false),
            EvidenceConflictCount = sample.RagLabEvidence.Count(HasConflict),
            UnverifiedEvidenceFieldCount = sample.RagLabEvidence.Sum(static item => item.UnverifiedItems.Count),
            ContainsInternalRagTerms = InternalRagMarkers.Any(marker => answer.Contains(marker, StringComparison.OrdinalIgnoreCase))
                || EvidenceNumberRegex().IsMatch(answer),
            HasConcreteSteps = ConcreteStepRegex().IsMatch(answer),
            HasJapaneseText = JapaneseTextRegex().IsMatch(answer),
            GenerationMilliseconds = Math.Max(0, (long)Math.Round(sample.GenerationDuration.TotalMilliseconds)),
        };
    }

    private static CodexAbAnswerability DetermineAnswerability(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return CodexAbAnswerability.NoAnswer;
        }

        return InsufficientEvidenceMarkers.Any(marker => answer.Contains(marker, StringComparison.OrdinalIgnoreCase))
            ? CodexAbAnswerability.InsufficientEvidence
            : CodexAbAnswerability.Answerable;
    }

    private static int CountConfirmationLines(string answer)
    {
        return answer.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(static line => line.Contains("要確認", StringComparison.OrdinalIgnoreCase)
                || line.Contains("確認が必要", StringComparison.OrdinalIgnoreCase)
                || line.Contains("追加確認", StringComparison.OrdinalIgnoreCase)
                || line.Contains("不足情報", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasConflict(RagLabEvidenceItem item)
    {
        return item.PossibleConflict == true
            || item.Warnings.Any(static warning => warning.Contains("矛盾", StringComparison.OrdinalIgnoreCase)
                || warning.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOfficial(string sourceType) =>
        sourceType.Contains("Official", StringComparison.OrdinalIgnoreCase);

    private static bool IsManual(string sourceType) =>
        !IsOfficial(sourceType) && sourceType.Contains("Manual", StringComparison.OrdinalIgnoreCase);

    private static bool IsPastCase(string sourceType) =>
        sourceType.Contains("PastCase", StringComparison.OrdinalIgnoreCase)
        || sourceType.Contains("PastAnswer", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeForKey(string? value) =>
        WhitespaceRegex().Replace(value?.Trim() ?? string.Empty, " ").ToUpperInvariant();

    [GeneratedRegex(@"(?im)^\s*(?:\d+[.)、]|[-*・]|手順\s*[:：]|操作\s*[:：])")]
    private static partial Regex ConcreteStepRegex();

    [GeneratedRegex(@"(?i)\bEvidence\s+\d+\b")]
    private static partial Regex EvidenceNumberRegex();

    [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]")]
    private static partial Regex JapaneseTextRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
