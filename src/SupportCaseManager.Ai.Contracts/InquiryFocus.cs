using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public sealed record class InquiryFocus
{
    [JsonPropertyName("focusText")]
    public string FocusText { get; init; } = string.Empty;

    [JsonPropertyName("importantTerms")]
    public IReadOnlyList<string> ImportantTerms { get; init; } = [];

    [JsonPropertyName("excludedTerms")]
    public IReadOnlyList<string> ExcludedTerms { get; init; } = [];

    [JsonPropertyName("targetVersions")]
    public IReadOnlyList<string> TargetVersions { get; init; } = [];

    [JsonPropertyName("isFreshnessSensitive")]
    public bool IsFreshnessSensitive { get; init; }

    [JsonPropertyName("freshnessReason")]
    public string FreshnessReason { get; init; } = string.Empty;

    [JsonPropertyName("primaryTopics")]
    public IReadOnlyList<InquiryTopicReference> PrimaryTopics { get; init; } = [];

    [JsonPropertyName("excludedTopics")]
    public IReadOnlyList<InquiryTopicReference> ExcludedTopics { get; init; } = [];

    [JsonPropertyName("requiredCoverage")]
    public IReadOnlyList<string> RequiredCoverage { get; init; } = [];

    // Recipient information is deliberately kept outside the query used for technical retrieval.
    [JsonPropertyName("recipientContext")]
    public RecipientContext RecipientContext { get; init; } = new();

    [JsonPropertyName("technicalQuery")]
    public TechnicalQuery TechnicalQuery { get; init; } = new();
}

public sealed record class RecipientContext
{
    [JsonPropertyName("companyName")]
    public string? CompanyName { get; init; }

    [JsonPropertyName("customerName")]
    public string? CustomerName { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("phone")]
    public string? Phone { get; init; }

    [JsonPropertyName("supportId")]
    public string? SupportId { get; init; }

    [JsonPropertyName("signature")]
    public string? Signature { get; init; }

    [JsonPropertyName("answerRecipient")]
    public string? AnswerRecipient { get; init; }
}

public sealed record class TechnicalQuery
{
    [JsonPropertyName("product")]
    public IReadOnlyList<string> Product { get; init; } = [];

    [JsonPropertyName("component")]
    public IReadOnlyList<string> Component { get; init; } = [];

    [JsonPropertyName("feature")]
    public IReadOnlyList<string> Feature { get; init; } = [];

    [JsonPropertyName("operation")]
    public IReadOnlyList<string> Operation { get; init; } = [];

    [JsonPropertyName("object")]
    public IReadOnlyList<string> Object { get; init; } = [];

    [JsonPropertyName("technology")]
    public IReadOnlyList<string> Technology { get; init; } = [];

    [JsonPropertyName("language")]
    public IReadOnlyList<string> Language { get; init; } = [];

    [JsonPropertyName("version")]
    public IReadOnlyList<string> Version { get; init; } = [];

    [JsonPropertyName("enginePack")]
    public IReadOnlyList<string> EnginePack { get; init; } = [];

    [JsonPropertyName("hotfix")]
    public IReadOnlyList<string> Hotfix { get; init; } = [];

    [JsonPropertyName("errorCode")]
    public IReadOnlyList<string> ErrorCode { get; init; } = [];

    [JsonPropertyName("command")]
    public IReadOnlyList<string> Command { get; init; } = [];

    [JsonPropertyName("option")]
    public IReadOnlyList<string> Option { get; init; } = [];

    [JsonPropertyName("fileExtension")]
    public IReadOnlyList<string> FileExtension { get; init; } = [];

    [JsonPropertyName("intent")]
    public IReadOnlyList<string> Intent { get; init; } = [];

    [JsonPropertyName("negatedTopics")]
    public IReadOnlyList<string> NegatedTopics { get; init; } = [];

    [JsonPropertyName("coreQuestion")]
    public string CoreQuestion { get; init; } = string.Empty;
}

public sealed record class InquiryTopicReference
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;
}
