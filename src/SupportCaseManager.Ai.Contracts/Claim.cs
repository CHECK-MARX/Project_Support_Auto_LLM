using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Contracts;

public static class ClaimSupportLevels
{
    public const string Supported = "Supported";
    public const string PartiallySupported = "PartiallySupported";
    public const string Conflicting = "Conflicting";
    public const string Unsupported = "Unsupported";
}

public sealed record class Claim
{
    [JsonPropertyName("statement")]
    public string Statement { get; init; } = string.Empty;

    [JsonPropertyName("supportingFactIds")]
    public IReadOnlyList<string> SupportingFactIds { get; init; } = [];

    [JsonPropertyName("supportLevel")]
    public string SupportLevel { get; init; } = ClaimSupportLevels.Unsupported;

    [JsonPropertyName("conflicting")]
    public bool Conflicting { get; init; }

    [JsonPropertyName("customerVisible")]
    public bool CustomerVisible { get; init; }
}
