using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Ranking;

[JsonConverter(typeof(JsonStringEnumConverter<TopicEntityKind>))]
public enum TopicEntityKind
{
    Product,
    Feature,
    Command,
    Option,
    Api,
    Setting,
    ErrorCode,
    File,
    Version,
    OperatingSystem,
    ServerType,
}

public sealed record TopicAliasDefinition
{
    public string CanonicalName { get; init; } = string.Empty;

    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public sealed record TopicEntityAliasDefinition
{
    public TopicEntityKind Kind { get; init; }

    public string CanonicalValue { get; init; } = string.Empty;

    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public sealed record TopicEntityCatalog
{
    public IReadOnlyList<TopicAliasDefinition> Products { get; init; } = [];

    public IReadOnlyList<TopicAliasDefinition> Components { get; init; } = [];

    public IReadOnlyList<TopicAliasDefinition> Features { get; init; } = [];

    public IReadOnlyList<TopicAliasDefinition> Objects { get; init; } = [];

    public IReadOnlyList<TopicEntityAliasDefinition> Entities { get; init; } = [];
}

public sealed record TopicEntityValue
{
    public TopicEntityKind Kind { get; init; }

    public string Value { get; init; } = string.Empty;

    public string NormalizedValue { get; init; } = string.Empty;
}

public sealed record TopicEntityProfile
{
    public IReadOnlyList<string> Products { get; init; } = [];

    public IReadOnlyList<string> Components { get; init; } = [];

    public IReadOnlyList<string> Features { get; init; } = [];

    public IReadOnlyList<string> Operations { get; init; } = [];

    public IReadOnlyList<string> Objects { get; init; } = [];

    public IReadOnlyList<string> Intents { get; init; } = [];

    public IReadOnlyList<TopicEntityValue> Entities { get; init; } = [];
}

public sealed record TopicConflictAssessment
{
    public bool TopicConflict { get; init; }

    public bool HasTopicMatch { get; init; }

    public bool NoTopicMatch { get; init; }

    public IReadOnlyList<string> ConflictKinds { get; init; } = [];

    public IReadOnlyList<string> MatchedProducts { get; init; } = [];

    public IReadOnlyList<string> MatchedComponents { get; init; } = [];

    public IReadOnlyList<string> MatchedFeatures { get; init; } = [];

    public IReadOnlyList<TopicEntityValue> MatchedEntities { get; init; } = [];
}
