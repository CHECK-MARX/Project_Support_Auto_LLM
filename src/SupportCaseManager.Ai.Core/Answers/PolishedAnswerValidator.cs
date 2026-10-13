using System.Text;
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
        InquiryFocus? focus,
        string? inquiryText = null)
    {
        if (!PreservesProtectedValues(allowedContext, polishedAnswer))
        {
            return false;
        }

        var required = CommandLine().Matches(deterministicAnswer)
            .Select(static match => match.Value.Trim())
            .Concat(HeaderName().Matches(deterministicAnswer)
                .Select(static match => match.Value.Trim()))
            .Concat((focus?.TargetVersions ?? []).Where(deterministicAnswer.Contains))
            .Concat((focus?.TechnicalQuery.Command ?? []).Where(deterministicAnswer.Contains))
            .Concat(ExtractInquiryTechnicalValues(inquiryText ?? string.Empty))
            .Distinct(StringComparer.Ordinal);
        return required.All(polishedAnswer.Contains);
    }

    public static IReadOnlyList<string> ExtractInquiryTechnicalValues(string inquiryText) =>
        HeaderName().Matches(inquiryText).Select(static match => match.Value.Trim())
            .Distinct(StringComparer.Ordinal).ToArray();

    // Only restore an unambiguous width/case variant of a header named in the
    // inquiry. Different letters (for example CR-O versus CROS) remain invalid.
    public static string RestoreUnambiguousInquiryHeader(string reply, string inquiryText)
    {
        var required = ExtractInquiryTechnicalValues(inquiryText);
        var missing = required.Where(value => !reply.Contains(value, StringComparison.Ordinal))
            .ToArray();
        if (missing.Length != 1) return reply;

        var generated = HeaderName().Matches(reply).Select(static match => match.Value.Trim())
            .Where(value => !required.Contains(value, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (generated.Length != 1 ||
            !string.Equals(generated[0].Normalize(NormalizationForm.FormKC).ToUpperInvariant(),
                missing[0].Normalize(NormalizationForm.FormKC).ToUpperInvariant(),
                StringComparison.Ordinal))
            return reply;
        return reply.Replace(generated[0], missing[0], StringComparison.Ordinal);
    }

    public static IReadOnlyList<string> ExtractProtectedValues(string answer)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in UrlOrVersion().Matches(answer)) values.Add(match.Value.TrimEnd('.', ',', '。'));
        foreach (Match match in CommandLine().Matches(answer)) values.Add(match.Value.Trim());
        foreach (Match match in HeaderName().Matches(answer)) values.Add(match.Value.Trim());
        foreach (Match match in DocumentReference().Matches(answer)) values.Add(match.Value);
        return values.ToList();
    }

    [GeneratedRegex(@"https?://[^\s)]+|(?<!\d)\d+(?:\.\d+){1,3}(?:[A-Za-z][\w.-]*)?(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlOrVersion();

    [GeneratedRegex(@"\b(?:qacli|qaclianalyze)(?:\s+[A-Za-z0-9_.:/<>${}-]+){0,10}", RegexOptions.IgnoreCase)]
    private static partial Regex CommandLine();

    [GeneratedRegex(@"(?<![A-Za-zＡ-Ｚａ-ｚ0-9０-９._-])[A-Za-zＡ-Ｚａ-ｚ][A-Za-zＡ-Ｚａ-ｚ0-9０-９._-]{1,31}\s*ヘッダー?", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"[^\r\n]{0,160}(?:Page\s+\d+|『[^』]+』)[^\r\n]{0,160}", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentReference();
}
