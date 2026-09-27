using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Answers;

public static partial class PolishedAnswerValidator
{
    public static bool PreservesProtectedValues(string deterministicAnswer, string polishedAnswer)
    {
        if (string.IsNullOrWhiteSpace(polishedAnswer)) return false;
        var deterministicValues = ExtractProtectedValues(deterministicAnswer)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var value in ExtractProtectedValues(polishedAnswer))
        {
            if (!deterministicValues.Contains(value)) return false;
        }

        return true;
    }

    public static bool PreservesProtectedValues(
        string allowedContext,
        string deterministicAnswer,
        string polishedAnswer,
        InquiryFocus? focus)
    {
        if (!PreservesProtectedValues(allowedContext, polishedAnswer))
        {
            return false;
        }

        var required = CommandLine().Matches(deterministicAnswer)
            .Select(static match => match.Value.Trim())
            .Concat((focus?.TargetVersions ?? []).Where(deterministicAnswer.Contains))
            .Concat((focus?.TechnicalQuery.Command ?? []).Where(deterministicAnswer.Contains))
            .Distinct(StringComparer.Ordinal);
        return required.All(polishedAnswer.Contains);
    }

    public static IReadOnlyList<string> ExtractProtectedValues(string answer)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in UrlOrVersion().Matches(answer)) values.Add(match.Value.TrimEnd('.', ',', '。'));
        foreach (Match match in CommandLine().Matches(answer)) values.Add(match.Value.Trim());
        foreach (Match match in DocumentReference().Matches(answer)) values.Add(match.Value);
        return values.ToList();
    }

    [GeneratedRegex(@"https?://[^\s)]+|(?<!\d)\d+(?:\.\d+){1,3}(?:[A-Za-z][\w.-]*)?(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlOrVersion();

    [GeneratedRegex(@"\b(?:qacli|qaclianalyze)(?:\s+[A-Za-z0-9_.:/<>${}-]+){0,10}", RegexOptions.IgnoreCase)]
    private static partial Regex CommandLine();

    [GeneratedRegex(@"[^\r\n]{0,160}(?:Page\s+\d+|『[^』]+』)[^\r\n]{0,160}", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentReference();
}
