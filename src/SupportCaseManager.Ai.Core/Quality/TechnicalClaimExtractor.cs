using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Core.Ranking;

namespace SupportCaseManager.Ai.Core.Quality;

public static partial class TechnicalClaimExtractor
{
    private static readonly HashSet<string> SafeAcronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "cli", "gui", "http", "https", "os",
    };

    public static IReadOnlyList<AnswerTechnicalClaim> Extract(
        string? text,
        TopicEntityCatalog? catalog = null)
    {
        var source = text ?? string.Empty;
        var claims = new List<AnswerTechnicalClaim>();
        AddMatches(claims, "Command", CommandRegex().Matches(source), major: true);
        AddMatches(claims, "Option", OptionRegex().Matches(source), major: true);
        AddMatches(claims, "Api", ApiRegex().Matches(source), major: true);
        AddMatches(claims, "Setting", SettingRegex().Matches(source), major: true);
        AddMatches(claims, "Path", WindowsPathRegex().Matches(source), major: true);
        AddMatches(claims, "Path", UnixPathRegex().Matches(source), major: true);
        AddMatches(claims, "File", FileRegex().Matches(source), major: false);
        AddMatches(claims, "ErrorCode", ErrorCodeRegex().Matches(source), major: true);
        AddMatches(claims, "Version", VersionRegex().Matches(source), major: true);
        AddMatches(claims, "Port", PortRegex().Matches(source), major: true);

        var profile = TopicEntityAnalyzer.Extract(source, catalog ?? new TopicEntityCatalog());
        foreach (var entity in profile.Entities)
        {
            if (entity.Kind is TopicEntityKind.Product or TopicEntityKind.Feature)
            {
                Add(claims, "ProductFeature", entity.Value, major: false);
            }
            else if (entity.Kind is TopicEntityKind.OperatingSystem or TopicEntityKind.ServerType)
            {
                Add(claims, entity.Kind.ToString(), entity.Value, major: false);
            }
        }

        return claims
            .Where(claim => !SafeAcronyms.Contains(claim.NormalizedValue))
            .DistinctBy(static claim => $"{claim.Kind}|{claim.NormalizedValue}", StringComparer.Ordinal)
            .ToList();
    }

    public static string Normalize(string kind, string value)
    {
        var normalized = TopicEntityAnalyzer.NormalizeText(value)
            .Trim(' ', '.', ',', ':', ';', '(', ')', '[', ']', '{', '}', '「', '」', '［', '］');
        if (kind == "Command")
        {
            normalized = CommandPrefixRegex().Replace(normalized, string.Empty).Trim();
        }
        else if (kind == "Version")
        {
            normalized = VersionPrefixRegex().Replace(normalized, string.Empty).Trim();
        }
        else if (kind == "Port")
        {
            normalized = PortPrefixRegex().Replace(normalized, string.Empty).Trim();
        }

        return WhitespaceRegex().Replace(normalized, " ");
    }

    private static void AddMatches(
        List<AnswerTechnicalClaim> claims,
        string kind,
        MatchCollection matches,
        bool major)
    {
        foreach (Match match in matches)
        {
            Add(claims, kind, match.Value, major);
        }
    }

    private static void Add(List<AnswerTechnicalClaim> claims, string kind, string value, bool major)
    {
        var normalized = Normalize(kind, value);
        if (normalized.Length == 0)
        {
            return;
        }

        claims.Add(new AnswerTechnicalClaim
        {
            Kind = kind,
            Value = value.Trim(),
            NormalizedValue = normalized,
            IsMajor = major,
        });
    }

    [GeneratedRegex(@"(?i)\bqacli(?:\s+[a-z][a-z0-9_.-]*){1,4}")]
    private static partial Regex CommandRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])--[A-Za-z][A-Za-z0-9_-]*")]
    private static partial Regex OptionRegex();

    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9_.-]*\s+API\b")]
    private static partial Regex ApiRegex();

    [GeneratedRegex(@"\b[A-Z][A-Z0-9_]{2,}\s*=\s*[A-Za-z0-9_.-]+\b")]
    private static partial Regex SettingRegex();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9_.-])[A-Za-z0-9_.-]+\.(?:json|xml|ya?ml|conf|cfg|ini|txt|log|pdf|qac|cct)(?=$|[^A-Za-z0-9_.-])")]
    private static partial Regex FileRegex();

    [GeneratedRegex("\\b[A-Za-z]:\\\\[^\\s\\\"'<>|]+")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.-])/(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+")]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(@"\b[A-Z]{2,10}-\d{2,8}\b")]
    private static partial Regex ErrorCodeRegex();

    [GeneratedRegex(@"(?i)(?:version|ver\.?|v|バージョン)\s*[:：]?\s*\d+(?:\.\d+){1,3}")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"(?i)(?:port|ポート)\s*[:：]?\s*\d{2,5}")]
    private static partial Regex PortRegex();

    [GeneratedRegex(@"(?i)^qacli\s+")]
    private static partial Regex CommandPrefixRegex();

    [GeneratedRegex(@"(?i)^(?:version|ver\.?|v|バージョン)\s*[:：]?\s*")]
    private static partial Regex VersionPrefixRegex();

    [GeneratedRegex(@"(?i)^(?:port|ポート)\s*[:：]?\s*")]
    private static partial Regex PortPrefixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
