using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Core.Cases;

namespace SupportCaseManager.App.Outlook;

public sealed record OutlookAcceptanceEnvelope(
    string EntryId, string StoreId, DateTime ReceivedAt, string Subject, string Body,
    string SenderSmtp, IReadOnlyList<string> ToSmtp, IReadOnlyList<string> CcSmtp)
{
    public string DelegatedSenderSmtp { get; init; } = string.Empty;
    public string InternetMessageId { get; init; } = string.Empty;
}

public sealed record OutlookAcceptanceCandidate(
    string MailKey, string EntryId, string StoreId, string BodyHash, DateTime ReceivedAt,
    string SupportId, string Company, string CustomerName, string CustomerEmail,
    string OriginalSubject, string OriginalSentAt, string InquiryBody, string EvidenceLocation)
{
    public bool HasOriginalMessage => OriginalSubject.Length > 0 && CustomerName.Length > 0
        && CustomerEmail.Length > 0 && OriginalSentAt.Length > 0 && InquiryBody.Length > 0;

    public static string BuildInquiryNote(string subject, string name, string email, string sentAt, string inquiry) =>
        $"件名: {subject.Trim()}\n送信者: {name.Trim()} <{email.Trim()}>\n"
        + $"受信日時: {sentAt.Trim()}\n\n【お問い合わせ内容】\n\n{inquiry.Trim()}";
}

public static class OutlookAcceptanceMail
{
    public const string CurrentUserSmtp = "itoke@toyo.co.jp";
    private static readonly Regex ReceiptId = new(
        @"本件は[\s\u3000]*、?[\s\u3000]*ID[\s\u3000]*[:：][\s\u3000]*(?<id>[0-9]{8})[\s\u3000]*で受け付けました[。．.]?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OriginalMarker = new(
        @"(?m)^[-]{3,}[\s\u3000]*Original Message[\s\u3000]*[-]{3,}[\s\u3000]*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Email = new(
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SentBy = new(
        @"^Sent by:\s*\S(?:.*\S)?\s+at\s+(?<email>[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static OutlookAcceptanceCandidate? Parse(OutlookAcceptanceEnvelope mail, Action<string>? trace = null)
    {
        if (string.IsNullOrWhiteSpace(mail.EntryId) || string.IsNullOrWhiteSpace(mail.StoreId)) return null;
        var body = mail.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var marker = OriginalMarker.Match(body);
        var receipt = marker.Success && marker.Index <= 3000 ? body[..marker.Index] : body[..Math.Min(body.Length, 1600)];
        var prefix = receipt.Length > 1600 ? receipt[..1600] : receipt;
        var match = ReceiptId.Match(prefix);
        if (!match.Success || match.Index > 400 || ReceiptId.Matches(prefix).Count != 1)
        {
            trace?.Invoke("ACCEPT_FILTERED=RECEIPT_ID_MISSING_OR_AMBIGUOUS");
            return null;
        }
        if (Regex.IsMatch(prefix[..match.Index], @"(?im)^(?:From|Sent|Subject|差出人|送信者|件名)\s*[:：]"))
        {
            trace?.Invoke("ACCEPT_FILTERED=QUOTED_RECEIPT");
            return null;
        }
        var id = CaseNaming.NormalizeSupportNumber(match.Groups["id"].Value);
        if (!Regex.IsMatch(id, "^[0-9]{8}$", RegexOptions.CultureInvariant)) return null;
        trace?.Invoke($"ACCEPT_SUPPORT_ID={id}");
        if (OutlookCaseMailClassifier.IsSalesforceCaseNotification(mail.SenderSmtp, mail.Subject, body, id))
        {
            trace?.Invoke($"ACCEPT_FILTERED={id}:SALESFORCE_NOTIFICATION");
            return null;
        }

        var evidence = CurrentUserEvidence(mail, body);
        if (evidence.Length == 0)
        {
            trace?.Invoke($"ACCEPT_FILTERED={id}:CURRENT_USER_EMAIL_MISSING");
            return null;
        }
        trace?.Invoke($"ACCEPT_CURRENT_USER_MATCH={id}:{evidence}");
        var fullTemplate = prefix.Contains("この度は、テクニカルサポートをご利用いただきまして", StringComparison.Ordinal)
            && prefix.Contains("追って、担当のエンジニアから回答を", StringComparison.Ordinal);
        var selfTestTemplate = mail.SenderSmtp.Equals(CurrentUserSmtp, StringComparison.OrdinalIgnoreCase)
            && (mail.ToSmtp.Contains(CurrentUserSmtp, StringComparer.OrdinalIgnoreCase)
                || mail.CcSmtp.Contains(CurrentUserSmtp, StringComparer.OrdinalIgnoreCase))
            && marker.Success && marker.Index <= 3000;
        if (!fullTemplate && !selfTestTemplate)
        {
            trace?.Invoke($"ACCEPT_FILTERED={id}:TOYO_TEMPLATE_MISSING");
            return null;
        }
        trace?.Invoke($"ACCEPT_TEMPLATE_MATCH={id}:{(fullTemplate ? "TOYO" : "SELF_TEST")}");
        var original = marker.Success && marker.Index <= 3000
            ? body[(marker.Index + marker.Length)..].TrimStart('\n', ' ', '\t') : string.Empty;
        var lines = original.Split('\n');
        string customerName = string.Empty, customerEmail = string.Empty, originalSubject = string.Empty, sentAt = string.Empty;
        var headerEnd = -1;
        for (var i = 0; i < Math.Min(lines.Length, 15); i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 && originalSubject.Length > 0) { headerEnd = i + 1; break; }
            var colon = line.IndexOfAny([':', '：']);
            if (colon < 0) continue;
            var label = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (label is "送信者" or "From")
            {
                customerEmail = Email.Match(value).Value.ToLowerInvariant();
                customerName = value[..Math.Max(0, value.IndexOf(customerEmail, StringComparison.OrdinalIgnoreCase))]
                    .Trim().TrimEnd('[', '<', ' ');
            }
            else if (label is "送信" or "Sent" or "Date") sentAt = value;
            else if (label is "件名" or "Subject") originalSubject = value;
        }
        var inquiry = headerEnd < 0 ? string.Empty : string.Join('\n', lines.Skip(headerEnd)).Trim();
        inquiry = Regex.Replace(inquiry, @"(?ms)\n\s*(?:<https://toyoss\.my\.salesforce\.com/servlet/servlet\.ImageServer\?.*|thread::[^\r\n]*::)\s*$", string.Empty).Trim();
        var company = receipt.Split('\n').Select(static line => line.Trim())
            .FirstOrDefault(static line => line.Length is > 2 and < 100
                && (line.Contains("株式会社", StringComparison.Ordinal) || line.Contains("有限会社", StringComparison.Ordinal))) ?? string.Empty;
        trace?.Invoke($"ACCEPT_CANDIDATE_CREATED={id}");
        return new(MailKey(mail.StoreId, mail.EntryId, mail.InternetMessageId), mail.EntryId, mail.StoreId, BodyHash(mail.Body),
            mail.ReceivedAt, id, company, customerName, customerEmail, originalSubject, sentAt, inquiry, evidence);
    }

    public static string MailKey(string storeId, string entryId, string internetMessageId = "") =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(storeId + ":"
            + (string.IsNullOrWhiteSpace(internetMessageId) ? entryId : internetMessageId.Trim().ToLowerInvariant()))));

    public static string BodyHash(string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static string CurrentUserEvidence(OutlookAcceptanceEnvelope mail, string body)
    {
        if (mail.SenderSmtp.Equals(CurrentUserSmtp, StringComparison.OrdinalIgnoreCase)) return "Sender/From";
        if (mail.DelegatedSenderSmtp.Equals(CurrentUserSmtp, StringComparison.OrdinalIgnoreCase)) return "SentBy";
        if (mail.ToSmtp.Contains(CurrentUserSmtp, StringComparer.OrdinalIgnoreCase)) return "To";
        if (mail.CcSmtp.Contains(CurrentUserSmtp, StringComparer.OrdinalIgnoreCase)) return "CC";
        var first = body.Split('\n').Select(static line => line.Trim()).FirstOrDefault(static line => line.Length > 0);
        return first is not null && SentBy.Match(first) is { Success: true } sent
            && sent.Groups["email"].Value.Equals(CurrentUserSmtp, StringComparison.OrdinalIgnoreCase)
            ? "SentBy" : string.Empty;
    }
}
