using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Facts;

public sealed partial class OfficialDocumentFactExtractor : IOfficialDocumentFactExtractor
{
    public IReadOnlyList<CandidateFact> Extract(AiOfficialDocumentIndexDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var facts = new List<CandidateFact>();
        foreach (var indexedDocument in document.Documents)
        {
            facts.AddRange(ExtractFromDocument(indexedDocument, document.BuiltAt));
        }

        return facts
            .GroupBy(static fact => $"{fact.Key}|{fact.Value}|{fact.SourceUrl}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderByDescending(fact => ConfidenceRank(fact.Confidence)).First())
            .OrderBy(static fact => fact.Key, StringComparer.Ordinal)
            .ThenByDescending(static fact => VersionSortKey(fact.Value))
            .ThenBy(static fact => fact.SourceUrl, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<CandidateFact> ExtractFromDocument(
        AiIndexedOfficialDocument document,
        DateTimeOffset extractedAt)
    {
        var haystack = string.Join(
            Environment.NewLine,
            document.Title,
            document.SectionTitle,
            document.Url,
            document.Text);
        var titleText = $"{document.Title} {document.SectionTitle} {document.Url}";
        if (IsScaPage(titleText))
        {
            yield break;
        }

        var isReleaseNotesPage = ContainsAny(titleText, "release notes", "release-notes");
        var isEnginePackPage = ContainsAny(titleText, "engine pack", "engine-pack");
        var isHotfixPage = ContainsAny(titleText, "hotfix", "hotfixes");

        if (isReleaseNotesPage && !isEnginePackPage && !isHotfixPage)
        {
            foreach (Match match in ReleaseNotesVersionRegex().Matches(titleText))
            {
                yield return CreateFact(
                    FactKeys.LatestSastVersion,
                    match.Groups["version"].Value,
                    document,
                    extractedAt,
                    "Release Notes title/url version");
            }

            foreach (Match match in CxSastVersionRegex().Matches(haystack))
            {
                yield return CreateFact(
                    FactKeys.LatestSastVersion,
                    match.Groups["version"].Value,
                    document,
                    extractedAt,
                    "CxSAST version in Release Notes official document");
            }
        }

        if (isEnginePackPage)
        {
            foreach (Match match in EnginePackVersionRegex().Matches(haystack))
            {
                yield return CreateFact(
                    FactKeys.LatestEnginePackVersion,
                    match.Groups["version"].Value,
                    document,
                    extractedAt,
                    "Engine Pack version in official Engine Pack document");
            }
        }

        if (isHotfixPage)
        {
            var hotfixes = HotfixRegex()
                .Matches(haystack)
                .Select(static match => match.Groups["hotfix"].Value.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(HotfixNumber)
                .ToList();
            if (hotfixes.Count > 0)
            {
                yield return CreateFact(
                    FactKeys.LatestHotfixVersion,
                    hotfixes[0],
                    document,
                    extractedAt,
                    "Highest Hotfix identifier in official Hotfix document");
            }
        }
    }

    private static CandidateFact CreateFact(
        string key,
        string value,
        AiIndexedOfficialDocument document,
        DateTimeOffset extractedAt,
        string reason)
    {
        return new CandidateFact
        {
            Key = key,
            Value = value.Trim(),
            Confidence = FactConfidences.High,
            SourceType = "OfficialDoc",
            SourceUrl = document.Url,
            Title = string.IsNullOrWhiteSpace(document.SectionTitle)
                ? document.Title
                : $"{document.Title} - {document.SectionTitle}",
            ExtractedAt = extractedAt,
            Reason = reason,
        };
    }

    private static int ConfidenceRank(string confidence)
    {
        return confidence switch
        {
            FactConfidences.High => 3,
            FactConfidences.Medium => 2,
            _ => 1,
        };
    }

    private static Version VersionSortKey(string value)
    {
        var match = VersionNumberRegex().Match(value);
        if (match.Success && Version.TryParse(match.Value, out var version))
        {
            return version;
        }

        return new Version(0, HotfixNumber(value));
    }

    private static int HotfixNumber(string value)
    {
        var match = HotfixRegex().Match(value);
        return match.Success && int.TryParse(match.Groups["number"].Value, out var number)
            ? number
            : 0;
    }

    private static bool ContainsAny(string value, params string[] keywords)
    {
        return keywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsScaPage(string titleText)
    {
        return ScaPageRegex().IsMatch(titleText) &&
            !titleText.Contains("CxSAST", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"release[-\s]?notes(?:\s+for)?\s+(?<version>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseNotesVersionRegex();

    [GeneratedRegex(@"\b(?:CxSAST|SAST)\s+(?<version>\d+\.\d+\.\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CxSastVersionRegex();

    [GeneratedRegex(@"engine\s*pack(?:\s+version)?\s+(?<version>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnginePackVersionRegex();

    [GeneratedRegex(@"\b(?<hotfix>HF(?<number>\d+))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HotfixRegex();

    [GeneratedRegex(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumberRegex();

    [GeneratedRegex(@"(?:\bSCA\b|checkmarx\s+sca|/sca(?:/|-|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScaPageRegex();
}
