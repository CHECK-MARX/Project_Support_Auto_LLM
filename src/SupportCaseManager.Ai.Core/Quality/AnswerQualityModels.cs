using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Core.Quality;

public sealed record AnswerQualityThresholds
{
    public double MinimumDirectness { get; init; } = 0.65;
    public double MinimumGrounding { get; init; } = 0.80;
    public double MinimumTopicAlignment { get; init; } = 0.75;
    public double MinimumCoverage { get; init; } = 0.80;
    public double MinimumTechnicalFidelity { get; init; } = 1.0;
    public double MinimumActionability { get; init; } = 0.65;
    public double MinimumCustomerReadiness { get; init; } = 0.65;
    public double InsufficientGrounding { get; init; } = 0.30;
    public double InsufficientCoverage { get; init; } = 0.34;

    public static AnswerQualityThresholds Default { get; } = new();
}

public sealed record AnswerQualityEvidence
{
    public string SourceId { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string? ProductName { get; init; }
    public string? Version { get; init; }
}

public sealed record AnswerQualityEvaluationInput
{
    public string Question { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string? ProductName { get; init; }
    public string? RequestedVersion { get; init; }
    public IReadOnlyList<AnswerQualityEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<string> ExistingInsufficientReasons { get; init; } = [];
    public TopicEntityCatalog Catalog { get; init; } = new();
}

public sealed record AnswerTechnicalClaim
{
    public string Kind { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string NormalizedValue { get; init; } = string.Empty;
    public bool IsMajor { get; init; }
}
