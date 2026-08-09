using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public static class AnswerQualityDecisions
{
    public const string CustomerReady = "CustomerReady";
    public const string NeedsReview = "NeedsReview";
    public const string InsufficientEvidence = "InsufficientEvidence";
    public const string Blocked = "Blocked";
}

public sealed record class UnsupportedTechnicalClaim
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("isMajor")]
    public bool IsMajor { get; init; }
}

public sealed record class AnswerQualityEvaluationResult
{
    [JsonPropertyName("directness")]
    public double Directness { get; init; }

    [JsonPropertyName("grounding")]
    public double Grounding { get; init; }

    [JsonPropertyName("topicAlignment")]
    public double TopicAlignment { get; init; }

    [JsonPropertyName("coverage")]
    public double Coverage { get; init; }

    [JsonPropertyName("evidenceCoverage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? EvidenceCoverage { get; init; }

    [JsonPropertyName("answerCoverage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AnswerCoverage { get; init; }

    [JsonPropertyName("requiredCoverage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiredCoverage { get; init; }

    [JsonPropertyName("missingEvidenceCoverage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? MissingEvidenceCoverage { get; init; }

    [JsonPropertyName("missingAnswerCoverage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? MissingAnswerCoverage { get; init; }

    [JsonPropertyName("technicalFidelity")]
    public double TechnicalFidelity { get; init; }

    [JsonPropertyName("unsupportedClaimCount")]
    public int UnsupportedClaimCount { get; init; }

    [JsonPropertyName("unsupportedTechnicalClaims")]
    public IReadOnlyList<UnsupportedTechnicalClaim> UnsupportedTechnicalClaims { get; init; } = [];

    [JsonPropertyName("conflictCount")]
    public int ConflictCount { get; init; }

    [JsonPropertyName("actionability")]
    public double Actionability { get; init; }

    [JsonPropertyName("customerReadiness")]
    public double CustomerReadiness { get; init; }

    [JsonPropertyName("internalLeakageCount")]
    public int InternalLeakageCount { get; init; }

    [JsonPropertyName("blockingReasons")]
    public IReadOnlyList<string> BlockingReasons { get; init; } = [];

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("decision")]
    public string Decision { get; init; } = AnswerQualityDecisions.NeedsReview;
}
