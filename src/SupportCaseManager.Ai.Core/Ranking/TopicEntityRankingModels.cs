namespace SupportCaseManager.Ai.Core.Ranking;

public sealed record TopicEntityRankingCandidate
{
    public int CandidateIndex { get; init; }

    public string CandidateId { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public string? ProductName { get; init; }

    public string? Version { get; init; }

    public double BaseSearchScore { get; init; }

    public double? LexicalScore { get; init; }

    public double? SemanticScore { get; init; }

    public int OriginalRank { get; init; }

    public bool IsManuallySelected { get; init; }

    public TopicEntityProfile Profile { get; init; } = new();
}

public sealed record TopicEntityRankingRequest
{
    public TopicEntityProfile QueryProfile { get; init; } = new();

    public TopicEntityProfile ExcludedProfile { get; init; } = new();

    public IReadOnlyList<string> TechnicalTokens { get; init; } = [];

    public string? RequestedProduct { get; init; }

    public string? RequestedVersion { get; init; }

    public IReadOnlyList<TopicEntityRankingCandidate> Candidates { get; init; } = [];

    public int MaxItems { get; init; } = 3;
}

public sealed record TopicEntityRankingAssessment
{
    public int CandidateIndex { get; init; }

    public string CandidateId { get; init; } = string.Empty;

    public double FinalScore { get; init; }

    public double TopicScore { get; init; }

    public double ProductScore { get; init; }

    public double ComponentScore { get; init; }

    public double FeatureScore { get; init; }

    public double OperationScore { get; init; }

    public double IntentScore { get; init; }

    public double EntityScore { get; init; }

    public double TechnicalTokenScore { get; init; }

    public double BaseSearchScore { get; init; }

    public double? LexicalScore { get; init; }

    public double? SemanticScore { get; init; }

    public double SourceTrustScore { get; init; }

    public double VersionScore { get; init; }

    public double ConflictPenalty { get; init; }

    public double ExclusionPenalty { get; init; }

    public bool ExplicitlyExcluded { get; init; }

    public bool? ProductMatch { get; init; }

    public string VersionMatch { get; init; } = "not_requested";

    public bool TopicConflict { get; init; }

    public bool HasTopicMatch { get; init; }

    public IReadOnlyList<string> ConflictKinds { get; init; } = [];

    public IReadOnlySet<string> Coverage { get; init; } = new HashSet<string>();

    public IReadOnlyList<string> ExactTechnicalTokens { get; init; } = [];

    public string TextFingerprint { get; init; } = string.Empty;

    public string SelectionReason { get; init; } = string.Empty;
}

public sealed record TopicEntityRankingResult
{
    public IReadOnlyList<TopicEntityRankingAssessment> Selected { get; init; } = [];

    public IReadOnlyList<TopicEntityRankingAssessment> Assessed { get; init; } = [];

    public IReadOnlySet<string> FinalCoverage { get; init; } = new HashSet<string>();

    public IReadOnlyList<string> InsufficientReasons { get; init; } = [];
}
