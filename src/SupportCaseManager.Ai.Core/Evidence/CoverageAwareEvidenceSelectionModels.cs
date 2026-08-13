namespace SupportCaseManager.Ai.Core.Evidence;

public sealed record CoverageEvidenceCandidate
{
    public string CandidateId { get; init; } = string.Empty;

    public int OriginalRank { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public string? DocumentTitle { get; init; }

    public string? DocumentId { get; init; }

    public string? FilePath { get; init; }

    public string? Section { get; init; }

    public string Text { get; init; } = string.Empty;

    public string? ContentHash { get; init; }

    public IReadOnlyList<string> TechnicalTokens { get; init; } = [];

    public IReadOnlyList<string> Coverage { get; init; } = [];

    public double RankingScore { get; init; }

    public double TopicScore { get; init; }

    public double EntityScore { get; init; }

    public double TechnicalTokenScore { get; init; }

    public double SourceTrust { get; init; }

    public double VersionScore { get; init; }

    public double ConflictPenalty { get; init; }

    public bool ExplicitlyExcluded { get; init; }

    public bool TopicConflict { get; init; }

    public bool ProductMismatch { get; init; }

    public bool IsManuallySelected { get; init; }

    public int EstimatedChars { get; init; }
}

public sealed record CoverageEvidenceSelectionRequest
{
    public IReadOnlyList<string> RequiredCoverage { get; init; } = [];

    public IReadOnlyList<string>? CorpusCoverage { get; init; }

    public IReadOnlyList<CoverageEvidenceCandidate> Candidates { get; init; } = [];

    public int BaseMaxItems { get; init; } = 3;

    public int ExpansionMaxItems { get; init; } = 5;

    public int CharacterBudget { get; init; } = 6000;

    public double MinimumQualityScore { get; init; } = 0.30;
}

public sealed record CoverageEvidenceSelectionResult
{
    public IReadOnlyList<CoverageEvidenceCandidate> Selected { get; init; } = [];

    public IReadOnlyList<CoverageEvidenceSelectionDecision> Decisions { get; init; } = [];

    public IReadOnlyList<string> RequiredCoverage { get; init; } = [];

    public IReadOnlyList<string> SearchCoverage { get; init; } = [];

    public IReadOnlyList<string> SelectedCoverage { get; init; } = [];

    public IReadOnlyList<string> MissingCoverage { get; init; } = [];

    public IReadOnlyList<string> Statuses { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int RedundantCandidatesSkipped { get; init; }

    public bool BudgetLimited { get; init; }

    public int EstimatedChars { get; init; }

    public string SelectionMode { get; init; } = "CoverageAware";
}

public sealed record CoverageEvidenceSelectionDecision
{
    public string CandidateId { get; init; } = string.Empty;

    public double QualityScore { get; init; }

    public double SetScore { get; init; }

    public IReadOnlyList<string> AddedCoverage { get; init; } = [];

    public bool IsManual { get; init; }

    public string Reason { get; init; } = string.Empty;
}
