using System.Net.Mail;
using System.Text.RegularExpressions;

namespace SupportCaseManager.App.Outlook;

internal static class OutlookMailMetadata
{
    internal static string HeaderValue(string headers, string name)
    {
        var unfolded = Regex.Replace(headers, @"\r?\n[ \t]+", " ");
        return Regex.Match(unfolded, $@"(?im)^{Regex.Escape(name)}:[ \t]*(?<value>[^\r\n]*)")
            .Groups["value"].Value.Trim();
    }

    internal static string OriginalSender(string headers)
    {
        var from = HeaderValue(headers, "From");
        return MailAddress.TryCreate(from, out var address) ? address.Address.ToLowerInvariant() : string.Empty;
    }

    internal static string AutomaticResponseReason(string subject, string body, string headers, string messageClass)
    {
        // Portals also mark human-authored replies as auto-generated. That value,
        // no-reply senders and X-Auto-Response-Suppress are not autoresponse evidence.
        var submitted = HeaderValue(headers, "Auto-Submitted").Split(';')[0].Trim();
        if (submitted.Equals("auto-replied", StringComparison.OrdinalIgnoreCase)) return "AUTO_SUBMITTED_AUTO_REPLIED";
        if (messageClass.StartsWith("IPM.Note.Rules.OofTemplate.", StringComparison.OrdinalIgnoreCase)) return "OUT_OF_OFFICE_MESSAGE_CLASS";
        foreach (var name in new[] { "X-Autoreply", "X-Autorespond" })
        {
            var value = HeaderValue(headers, name);
            if (value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return "AUTO_REPLY_HEADER";
        }

        var title = subject.Trim();
        // Do not strip Re:/Fw:. A person can reply to a reminder or receipt thread.
        if (Regex.IsMatch(title, @"^(?:(?:automatic|auto)[ -]?(?:reply|response)|out of (?:the )?office|自動返信|自動応答|不在通知)\s*[:：\[]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "AUTO_REPLY_SUBJECT";
        if (Regex.IsMatch(title, @"^\[(?:request received|ticket received|case created|case received)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "RECEIPT_SUBJECT";
        if (Regex.IsMatch(title, @"^Your (?:support )?(?:request|ticket|case) (?:has been|was) (?:received|created)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "RECEIPT_SUBJECT";

        var lead = string.Join('\n', body.Split('\n').TakeWhile(line =>
                !Regex.IsMatch(line.Trim(), @"^(?:>|-{2,}\s*(?:Original Message|元のメッセージ)|From\s*:|差出人\s*[:：])", RegexOptions.IgnoreCase)));
        lead = lead[..Math.Min(lead.Length, 2000)];
        if (title.StartsWith("Reminder:", StringComparison.OrdinalIgnoreCase)
            && title.Contains("not received any response", StringComparison.OrdinalIgnoreCase)
            && lead.Contains("not received any response", StringComparison.OrdinalIgnoreCase)) return "AUTOMATED_REMINDER";
        if (Regex.IsMatch(lead, @"(?im)^\s*(?:This is an (?:automated|automatic) (?:reply|response|acknowledg(?:e)?ment)|このメールは自動(?:返信|応答)|本メールは自動(?:返信|応答))",
                RegexOptions.CultureInvariant)) return "AUTO_REPLY_BODY";
        return string.Empty;
    }
}
