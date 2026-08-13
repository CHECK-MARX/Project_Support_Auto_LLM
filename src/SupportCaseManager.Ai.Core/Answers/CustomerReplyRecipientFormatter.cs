using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Answers;

public static partial class CustomerReplyRecipientFormatter
{
    public static string EnsureHeader(CaseContext context, string? customerReply)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(customerReply))
        {
            return string.Empty;
        }

        var companyName = Normalize(context.CompanyName);
        if (IsInternalCompany(companyName))
        {
            companyName = "[会社名]";
        }

        var customerName = Normalize(context.CustomerName);
        if (IsMissingCustomerName(customerName))
        {
            customerName = "[お客様名] 様";
        }
        else if (!HasHonorific(customerName))
        {
            customerName += " 様";
        }

        var body = RemoveInvalidLeadingRecipient(customerReply);
        var firstLines = SplitLines(body)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Take(4)
            .Select(static line => line.Trim())
            .ToList();
        if (firstLines.Contains(companyName, StringComparer.Ordinal) &&
            firstLines.Contains(customerName, StringComparer.Ordinal))
        {
            return body;
        }

        return $"{companyName}{Environment.NewLine}{customerName}{Environment.NewLine}{Environment.NewLine}{body.TrimStart()}";
    }

    private static string RemoveInvalidLeadingRecipient(string value)
    {
        var lines = SplitLines(value).ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        if (lines.Count > 0 && IsInternalCompanyOrRecipientLine(Normalize(lines[0])))
        {
            lines.RemoveAt(0);
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
            {
                lines.RemoveAt(0);
            }

            if (lines.Count > 0 && IsGenericCustomerLine(Normalize(lines[0])))
            {
                lines.RemoveAt(0);
            }
        }
        else if (lines.Count > 0 && IsGenericCustomerLine(Normalize(lines[0])))
        {
            lines.RemoveAt(0);
        }

        return string.Join(Environment.NewLine, lines).TrimStart();
    }

    private static IEnumerable<string> SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : WhitespaceRegex().Replace(value.Trim(), " ");

    private static bool IsInternalCompanyOrRecipientLine(string value) =>
        IsInternalCompany(value) ||
        value.StartsWith("TOYO ", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("東陽テクニカ ", StringComparison.Ordinal) ||
        value.StartsWith("株式会社東陽テクニカ ", StringComparison.Ordinal);

    private static bool IsInternalCompany(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value, "TOYO", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "TOYO Corporation", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "東陽テクニカ", StringComparison.Ordinal) ||
        string.Equals(value, "株式会社東陽テクニカ", StringComparison.Ordinal) ||
        string.Equals(value, "サンプル", StringComparison.Ordinal) ||
        string.Equals(value, "株式会社サンプル", StringComparison.Ordinal);

    private static bool IsMissingCustomerName(string value) =>
        string.IsNullOrWhiteSpace(value) || IsGenericCustomerLine(value) || IsInternalCompany(value);

    private static bool IsGenericCustomerLine(string value) =>
        string.Equals(value, "ご担当者", StringComparison.Ordinal) ||
        string.Equals(value, "ご担当者様", StringComparison.Ordinal) ||
        string.Equals(value, "担当者", StringComparison.Ordinal) ||
        string.Equals(value, "担当者様", StringComparison.Ordinal) ||
        string.Equals(value, "お客様", StringComparison.Ordinal) ||
        string.Equals(value, "お客様様", StringComparison.Ordinal);

    private static bool HasHonorific(string value) =>
        value.EndsWith("様", StringComparison.Ordinal) ||
        value.EndsWith("御中", StringComparison.Ordinal) ||
        value.EndsWith("殿", StringComparison.Ordinal);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
