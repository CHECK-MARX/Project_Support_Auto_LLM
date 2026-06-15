using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class FactResolutionResult
{
    [JsonPropertyName("classification")]
    public QuestionClassificationResult Classification { get; init; } = new();

    [JsonPropertyName("candidateFacts")]
    public IReadOnlyList<CandidateFact> CandidateFacts { get; init; } = [];

    [JsonPropertyName("resolvedFacts")]
    public IReadOnlyList<ResolvedFact> ResolvedFacts { get; init; } = [];

    [JsonPropertyName("answerReadiness")]
    public string AnswerReadiness { get; init; } = "InsufficientEvidence";

    [JsonPropertyName("missingFacts")]
    public IReadOnlyList<string> MissingFacts { get; init; } = [];

    [JsonPropertyName("conflicts")]
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    [JsonPropertyName("crawlerConflicts")]
    public IReadOnlyList<string> CrawlerConflicts { get; init; } = [];

    [JsonPropertyName("llmPromptUsesResolvedFacts")]
    public bool LlmPromptUsesResolvedFacts { get; init; }
}
