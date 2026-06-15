using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

public sealed record class AiManualIndexDocument
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("builtAt")]
    public DateTimeOffset BuiltAt { get; init; }

    [JsonPropertyName("sourceFolder")]
    public string SourceFolder { get; init; } = string.Empty;

    [JsonPropertyName("manuals")]
    public IReadOnlyList<AiIndexedManual> Manuals { get; init; } = [];
}
