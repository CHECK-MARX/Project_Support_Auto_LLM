using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Ranking;

public static partial class TopicEntityAnalyzer
{
    private static readonly (string Name, string[] Terms)[] OperationTerms =
    [
        ("Configuration", ["設定", "構成", "configure", "configuration", "setup", "set up"]),
        ("Creation", ["作成", "生成", "create", "creation", "generate"]),
        ("Upload", ["アップロード", "upload"]),
        ("Verification", ["確認", "検証", "verify", "verification", "check"]),
        ("Association", ["関連付け", "紐付け", "associate", "association", "link"]),
        ("Analysis", [
            "qacli analyze", "プロジェクトを解析", "プロジェクトの解析", "解析を実行", "解析の実行",
            "解析する", "解析開始", "analyze project", "project analysis", "run analysis", "execute analysis",
        ]),
        ("Execution", ["実行", "起動", "run", "execute", "launch"]),
        ("Troubleshooting", ["原因", "対処", "解決", "troubleshoot", "failure", "failed"]),
    ];

    private static readonly (string Name, string[] Terms)[] IntentTerms =
    [
        ("Overview", ["概要", "何ですか", "とは", "what is", "overview"]),
        ("Purpose", ["用途", "目的", "何に使", "purpose", "use case"]),
        ("HowTo", ["方法", "手順", "やり方", "how to", "procedure"]),
        ("Command", ["コマンド", "command", "cli", "qacli"]),
        ("Configuration", ["設定", "構成", "configuration", "configure", "setup"]),
        ("Verification", ["確認", "検証", "verify", "verification"]),
        ("Troubleshooting", ["原因", "対処", "解決", "troubleshoot", "error", "failed"]),
    ];

    private static readonly (string Canonical, string[] Aliases)[] OperatingSystems =
    [
        ("Windows", ["windows"]),
        ("Linux", ["linux"]),
        ("macOS", ["macos", "mac os"]),
        ("RHEL", ["rhel", "red hat enterprise linux"]),
        ("Ubuntu", ["ubuntu"]),
    ];

    private static readonly (string Canonical, string[] Aliases)[] ServerTypes =
    [
        ("License Server", ["license server", "ライセンスサーバー", "ライセンスサーバ"]),
        ("Application Server", ["application server", "アプリケーションサーバー", "アプリケーションサーバ"]),
        ("Web Server", ["web server", "webサーバー", "webサーバ"]),
        ("Database Server", ["database server", "db server", "データベースサーバー", "データベースサーバ"]),
    ];

    public static TopicEntityProfile Extract(string? text, TopicEntityCatalog? catalog = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TopicEntityProfile();
        }

        catalog ??= new TopicEntityCatalog();
        var normalizedText = NormalizeText(text);
        var products = MatchAliases(normalizedText, catalog.Products);
        var components = MatchAliases(normalizedText, catalog.Components);
        var features = MatchAliases(normalizedText, catalog.Features);
        var objects = MatchAliases(normalizedText, catalog.Objects);
        var operations = MatchNamedTerms(normalizedText, OperationTerms);
        var intents = MatchNamedTerms(normalizedText, IntentTerms);
        var entities = new List<TopicEntityValue>();

        foreach (var definition in catalog.Entities)
        {
            if (Aliases(definition.CanonicalValue, definition.Aliases)
                .Any(alias => ContainsAlias(normalizedText, NormalizeText(alias))))
            {
                AddEntity(entities, definition.Kind, definition.CanonicalValue);
            }
        }

        AddRegexEntities(entities, text);
        AddKnownEntities(entities, normalizedText, TopicEntityKind.OperatingSystem, OperatingSystems);
        AddKnownEntities(entities, normalizedText, TopicEntityKind.ServerType, ServerTypes);

        foreach (var product in products)
        {
            AddEntity(entities, TopicEntityKind.Product, product);
        }

        foreach (var feature in features)
        {
            AddEntity(entities, TopicEntityKind.Feature, feature);
        }

        return new TopicEntityProfile
        {
            Products = products,
            Components = components,
            Features = features,
            Operations = operations,
            Objects = objects,
            Intents = intents,
            Entities = entities,
        };
    }

    public static TopicConflictAssessment Compare(
        TopicEntityProfile query,
        TopicEntityProfile evidence)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(evidence);

        var products = Intersect(query.Products, evidence.Products);
        var components = Intersect(query.Components, evidence.Components);
        var features = Intersect(query.Features, evidence.Features);
        var operations = Intersect(query.Operations, evidence.Operations);
        if (query.Features.Contains("Project Analysis", StringComparer.OrdinalIgnoreCase) &&
            evidence.Operations.Contains("Analysis", StringComparer.OrdinalIgnoreCase) &&
            !features.Contains("Project Analysis", StringComparer.OrdinalIgnoreCase))
        {
            // "qacli analyze" is the operation form of the Project Analysis feature.
            // Treat both forms as equivalent for focused evidence eligibility.
            features = features.Concat(["Project Analysis"]).ToArray();
        }
        var entities = IntersectEntities(query.Entities, evidence.Entities);
        var conflicts = new List<string>();

        AddConflict(conflicts, "Product", query.Products, evidence.Products, products);
        AddConflict(conflicts, "Component", query.Components, evidence.Components, components);
        AddConflict(conflicts, "Feature", query.Features, evidence.Features, features);

        var queryHasSpecificTopic = query.Features.Count > 0 || query.Components.Count > 0;
        var hasTopicMatch = features.Count > 0 ||
            (query.Features.Count == 0 && components.Count > 0) ||
            (query.Features.Count == 0 && query.Components.Count == 0 && products.Count > 0);

        return new TopicConflictAssessment
        {
            TopicConflict = conflicts.Count > 0,
            HasTopicMatch = hasTopicMatch,
            NoTopicMatch = queryHasSpecificTopic && !hasTopicMatch,
            ConflictKinds = conflicts,
            MatchedProducts = products,
            MatchedComponents = components,
            MatchedFeatures = features,
            MatchedOperations = operations,
            MatchedEntities = entities,
        };
    }

    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        return WhitespaceRegex().Replace(normalized, " ").Trim();
    }

    public static string NormalizeEntityValue(TopicEntityKind kind, string? value)
    {
        var normalized = NormalizeText(value);
        if (kind == TopicEntityKind.Command && normalized.StartsWith("qacli ", StringComparison.Ordinal))
        {
            normalized = normalized["qacli ".Length..].Trim();
        }

        return normalized;
    }

    private static IReadOnlyList<string> MatchAliases(
        string normalizedText,
        IReadOnlyList<TopicAliasDefinition> definitions)
    {
        return definitions
            .Where(definition => !string.IsNullOrWhiteSpace(definition.CanonicalName))
            .Where(definition => Aliases(definition.CanonicalName, definition.Aliases)
                .Any(alias => ContainsAlias(normalizedText, NormalizeText(alias))))
            .Select(definition => definition.CanonicalName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> MatchNamedTerms(
        string normalizedText,
        IEnumerable<(string Name, string[] Terms)> definitions)
    {
        return definitions
            .Where(definition => definition.Terms
                .Any(term => ContainsAlias(normalizedText, NormalizeText(term))))
            .Select(definition => definition.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> Aliases(string canonical, IReadOnlyList<string> aliases)
    {
        yield return canonical;
        foreach (var alias in aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                yield return alias;
            }
        }
    }

    private static bool ContainsAlias(string normalizedText, string normalizedAlias)
    {
        if (string.IsNullOrWhiteSpace(normalizedAlias))
        {
            return false;
        }

        if (normalizedAlias.All(character => character <= 0x7f) &&
            char.IsLetterOrDigit(normalizedAlias[0]) &&
            char.IsLetterOrDigit(normalizedAlias[^1]))
        {
            return Regex.IsMatch(
                normalizedText,
                $@"(?<![a-z0-9]){Regex.Escape(normalizedAlias)}(?![a-z0-9])",
                RegexOptions.CultureInvariant);
        }

        return normalizedText.Contains(normalizedAlias, StringComparison.Ordinal);
    }

    private static void AddRegexEntities(List<TopicEntityValue> entities, string text)
    {
        AddMatches(entities, TopicEntityKind.Command, CommandRegex().Matches(text));
        AddMatches(entities, TopicEntityKind.Option, OptionRegex().Matches(text));
        AddMatches(entities, TopicEntityKind.Api, ApiRegex().Matches(text));
        AddMatches(entities, TopicEntityKind.Setting, SettingRegex().Matches(text));
        AddMatches(entities, TopicEntityKind.ErrorCode, ErrorCodeRegex().Matches(text));
        AddMatches(entities, TopicEntityKind.File, FileRegex().Matches(text));
        AddMatches(entities, TopicEntityKind.Version, VersionRegex().Matches(text));
    }

    private static void AddMatches(
        List<TopicEntityValue> entities,
        TopicEntityKind kind,
        MatchCollection matches)
    {
        foreach (Match match in matches)
        {
            AddEntity(entities, kind, match.Value);
        }
    }

    private static void AddKnownEntities(
        List<TopicEntityValue> entities,
        string normalizedText,
        TopicEntityKind kind,
        IEnumerable<(string Canonical, string[] Aliases)> definitions)
    {
        foreach (var definition in definitions)
        {
            if (definition.Aliases.Any(alias => ContainsAlias(normalizedText, NormalizeText(alias))))
            {
                AddEntity(entities, kind, definition.Canonical);
            }
        }
    }

    private static void AddEntity(
        List<TopicEntityValue> entities,
        TopicEntityKind kind,
        string value)
    {
        var normalized = NormalizeEntityValue(kind, value);
        if (string.IsNullOrWhiteSpace(normalized) ||
            entities.Any(entity => entity.Kind == kind && entity.NormalizedValue == normalized))
        {
            return;
        }

        entities.Add(new TopicEntityValue
        {
            Kind = kind,
            Value = value.Trim(),
            NormalizedValue = normalized,
        });
    }

    private static IReadOnlyList<string> Intersect(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var rightValues = right.Select(NormalizeText).ToHashSet(StringComparer.Ordinal);
        return left
            .Where(value => rightValues.Contains(NormalizeText(value)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<TopicEntityValue> IntersectEntities(
        IReadOnlyList<TopicEntityValue> left,
        IReadOnlyList<TopicEntityValue> right)
    {
        var rightValues = right
            .Select(entity => (entity.Kind, NormalizeEntityValue(entity.Kind, entity.Value)))
            .ToHashSet();
        return left
            .Where(entity => rightValues.Contains(
                (entity.Kind, NormalizeEntityValue(entity.Kind, entity.Value))))
            .ToList();
    }

    private static void AddConflict(
        List<string> conflicts,
        string kind,
        IReadOnlyList<string> queryValues,
        IReadOnlyList<string> evidenceValues,
        IReadOnlyList<string> matches)
    {
        if (queryValues.Count > 0 && evidenceValues.Count > 0 && matches.Count == 0)
        {
            conflicts.Add(kind);
        }
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])qacli(?:\s+[A-Za-z0-9_.+/-]+){1,3}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])--[A-Za-z0-9][A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex OptionRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])[A-Za-z][A-Za-z0-9_.]*(?:\s+)?(?:API|Api)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex ApiRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:[A-Z][A-Z0-9]*_[A-Z0-9_]+(?:\s*=\s*[^\s,;]+)?|[A-Z][A-Z0-9_]{2,}\s*=\s*[^\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SettingRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])[A-Z]{2,10}[-_]\d{2,8}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodeRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.-])[A-Za-z0-9_.-]+\.(?:json|xml|yaml|yml|ini|conf|cfg|log|txt|csv|pdf|zip)(?![A-Za-z0-9_.-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])v?\d+(?:\.\d+){1,3}(?![A-Za-z0-9_.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
