using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Codex;

public sealed record RagLabEvidenceItem
{
    [JsonPropertyName("sourceType")]
    public string? SourceType { get; init; }

    [JsonPropertyName("documentId")]
    public string? DocumentId { get; init; }

    [JsonPropertyName("supportId")]
    public string? SupportId { get; init; }

    [JsonPropertyName("product")]
    public string? Product { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("selectionReason")]
    public string? SelectionReason { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("productMatch")]
    public bool? ProductMatch { get; init; }

    [JsonPropertyName("versionMatch")]
    public bool? VersionMatch { get; init; }

    [JsonPropertyName("keywordMatches")]
    public IReadOnlyList<string> KeywordMatches { get; init; } = [];

    [JsonPropertyName("possiblyStale")]
    public bool? PossiblyStale { get; init; }

    [JsonPropertyName("possibleConflict")]
    public bool? PossibleConflict { get; init; }

    [JsonPropertyName("unverifiedItems")]
    public IReadOnlyList<string> UnverifiedItems { get; init; } = [];
}

public sealed record RagLabEvidenceLoadRequest
{
    public bool IsEnabled { get; init; }
    public string EvidenceFilePath { get; init; } = string.Empty;
    public string BaselineReadinessFilePath { get; init; } = string.Empty;
    public int MaxItems { get; init; } = 3;
    public string? ExpectedProduct { get; init; }
    public string? ExpectedVersion { get; init; }
}

public sealed record RagLabEvidenceLoadResult
{
    public bool IsEnabled { get; init; }
    public bool IsBaselineReady { get; init; }
    public string Query { get; init; } = string.Empty;
    public IReadOnlyList<RagLabEvidenceItem> Evidence { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? FallbackReason { get; init; }
    public bool HasEvidence => Evidence.Count > 0;
}
