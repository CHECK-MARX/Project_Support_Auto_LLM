using System.Globalization;
using System.Text.RegularExpressions;
using SupportCaseManager.Core.Cases;

namespace SupportCaseManager.App.Outlook;

public sealed record OutlookCaseKey(string Product, string SupportId, string FolderPath)
{
    public string NormalizedSupportId => CaseNaming.NormalizeSupportNumber(SupportId);
}

public sealed record OutlookMailReference(
    string EntryId,
    string StoreId,
    DateTime Time,
    string Subject,
    string Sender,
    string Recipients,
    string SupportId,
    bool IsInbound)
{
    public string BodyHash { get; init; } = string.Empty;
    public bool IsRelayedOutbound { get; init; }
}

public sealed record OutlookMailCandidate(
    OutlookMailReference Mail,
    bool IsInbound,
    IReadOnlyList<string> Addresses)
{
    public IReadOnlyList<string> CopyAddresses { get; init; } = [];
    public bool IsSystemNotification { get; init; }
    public string NotificationReason { get; init; } = string.Empty;
    public bool IsLinkedCustomerInquiry { get; init; }
    public bool IsRelayedOutbound { get; init; }
    public string OriginalSender { get; init; } = string.Empty;
    public bool IsAutomaticResponse { get; init; }
    public string AutomaticResponseReason { get; init; } = string.Empty;
    public OutlookManufacturerMessageKind ManufacturerMessageKind { get; init; }
    public string ManufacturerMessageSummary { get; init; } = string.Empty;
}

public sealed record OutlookCaseContacts(
    IReadOnlySet<string> Customer,
    IReadOnlySet<string> Manufacturer);

public sealed record OutlookStatusLine(string Text, OutlookMailReference? Mail = null)
{
    public string? Explanation { get; init; }
    public bool IsInformational { get; init; }
}

public sealed record OutlookCaseMailStatus(
    OutlookMailReference? LatestCustomerInbound,
    OutlookMailReference? LatestCustomerOutbound,
    OutlookMailReference? LatestManufacturerInbound,
    OutlookMailReference? LatestManufacturerOutbound,
    OutlookMailReference? LatestUnclassified,
    string WorkflowState,
    OutlookStatusLine Primary,
    OutlookStatusLine Secondary)
{
    public string ClassificationReason { get; init; } = string.Empty;
    public int SkippedSystemNotificationCount { get; init; }
    public OutlookMailReference? LatestAutomaticResponse { get; init; }
    public int AutomaticResponseCount { get; init; }
    public OutlookStatusLine Notification { get; init; } = new(string.Empty);
    public DateTime? CheckedAtUtc { get; init; }
    public string RefreshError { get; init; } = string.Empty;

    public static OutlookCaseMailStatus Message(string message) =>
        new(null, null, null, null, null, "UNAVAILABLE", new OutlookStatusLine(message), new OutlookStatusLine(string.Empty));
}

public static class OutlookCaseMailClassifier
{
    private static readonly HashSet<string> PublicMailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "outlook.com", "hotmail.com", "yahoo.co.jp", "yahoo.com", "icloud.com",
        "salesforce.com", "mail.com", "proton.me", "protonmail.com", "live.com", "msn.com",
        "aol.com", "gmx.com", "example.com", "example.org", "example.net"
    };

    private enum Party { Unknown, Customer, Manufacturer }
    private sealed record PartyMatch(Party Party, string Reason, int Rank);

    internal static bool CanInferDomain(string domain) => domain.Length > 0
        && !PublicMailDomains.Contains(domain)
        && !domain.Equals("toyo.co.jp", StringComparison.OrdinalIgnoreCase);

    public static bool ContainsExactSupportId(string? text, string supportId)
    {
        var normalized = CaseNaming.NormalizeSupportNumber(supportId);
        return normalized.Length > 0 && !string.IsNullOrEmpty(text)
            && Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(normalized)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool IsSalesforceCaseNotification(string sender, string subject, string body, string supportId,
        IReadOnlyList<string>? externalRecipients = null)
    {
        var id = CaseNaming.NormalizeSupportNumber(supportId);
        return id.Length > 0
            && sender.Equals("noreply@salesforce.com", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(subject, $@"^新規ケース関連メール受信のお知らせ[。．.]?\s*ケース番号\s*{Regex.Escape(id)}\s*$")
            && Regex.IsMatch(body, $@"ケース番号\s*{Regex.Escape(id)}\s*のメールを受信しました。")
            && body.Contains("リンクをクリックして確認し、返信してください。", StringComparison.Ordinal)
            && body.Contains("toyoss.my.salesforce.com", StringComparison.OrdinalIgnoreCase)
            && (externalRecipients is null || externalRecipients.Count == 0);
    }

    public static bool IsSalesforceRelayedOutbound(string sender, string body, IReadOnlySet<string> currentUserAddresses)
    {
        if (!sender.Equals("noreply@salesforce.com", StringComparison.OrdinalIgnoreCase)) return false;
        var firstLine = body.Split('\n').Select(static line => line.Trim().TrimStart('\uFEFF'))
            .FirstOrDefault(static line => line.Length > 0);
        if (firstLine is null) return false;
        var match = Regex.Match(firstLine,
            @"^Sent by:\s*\S(?:.*\S)?\s+at\s+(?<email>[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && currentUserAddresses.Contains(match.Groups["email"].Value);
    }

    public static OutlookCaseMailStatus Build(
        OutlookCaseKey key,
        OutlookCaseContacts contacts,
        IEnumerable<OutlookMailCandidate> candidates,
        DateTime? now = null)
    {
        var caseCandidates = candidates.Where(item => string.Equals(item.Mail.SupportId, key.NormalizedSupportId,
            StringComparison.OrdinalIgnoreCase)).ToList();
        var incomingContacts = WithGroundedManufacturerRecipients(key, contacts, caseCandidates);
        var matched = caseCandidates
            .Where(static item => !item.IsSystemNotification && !item.IsAutomaticResponse)
            .OrderByDescending(item => item.Mail.Time)
            .ToList();
        OutlookMailReference? customerIn = null, customerOut = null, manufacturerIn = null, manufacturerOut = null, unclassified = null;
        var reasons = new Dictionary<OutlookMailReference, string>();
        var manufacturerContent = new Dictionary<OutlookMailReference, OutlookMailCandidate>();
        foreach (var candidate in matched)
        {
            var sourceAddresses = candidate.IsRelayedOutbound ? candidate.CopyAddresses
                : candidate.IsInbound && candidate.Mail.Sender.Equals("noreply@salesforce.com", StringComparison.OrdinalIgnoreCase)
                ? candidate.OriginalSender.Length > 0 ? [candidate.OriginalSender] : candidate.CopyAddresses
                : candidate.Addresses.Concat(candidate.CopyAddresses);
            var addresses = sourceAddresses
                .Where(static address => !string.IsNullOrWhiteSpace(address))
                .Select(static address => address.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // A copied recipient is not proof of who authored a portal message.
            // Published support identities can supplement missing case contacts
            // only for an actual inbound sender or explicit outbound recipients.
            var senderProduct = !candidate.IsInbound || !candidate.Mail.Sender.Equals("noreply@salesforce.com", StringComparison.OrdinalIgnoreCase)
                || candidate.OriginalSender.Length > 0 ? key.Product : null;
            var matches = addresses.Select(address => ClassifyAddress(address, candidate.IsInbound ? incomingContacts : contacts,
                candidate.IsRelayedOutbound ? key.Product : null, senderProduct, !candidate.IsInbound)).ToArray();
            var parties = matches.Select(static match => match.Party).Distinct().ToArray();
            if (parties.Length != 1 || parties[0] == Party.Unknown)
            {
                unclassified ??= candidate.Mail;
                reasons[candidate.Mail] = "UNKNOWN";
            }
            else if (parties[0] == Party.Customer)
            {
                reasons[candidate.Mail] = candidate.IsLinkedCustomerInquiry
                    ? "CASE_INQUIRY_MATCH" : matches.MaxBy(static match => match.Rank)!.Reason;
                if (candidate.IsInbound) customerIn ??= candidate.Mail;
                else customerOut ??= candidate.Mail;
            }
            else
            {
                reasons[candidate.Mail] = matches.MaxBy(static match => match.Rank)!.Reason;
                if (candidate.IsInbound)
                {
                    manufacturerIn ??= candidate.Mail;
                    manufacturerContent[candidate.Mail] = candidate;
                }
                else manufacturerOut ??= candidate.Mail;
            }
        }

        var clock = now ?? DateTime.Now;
        var automatic = caseCandidates.Where(static item => item.IsInbound && item.IsAutomaticResponse && !item.IsSystemNotification)
            .OrderByDescending(static item => item.Mail.Time).ToArray();
        var latestAutomatic = automatic.FirstOrDefault();
        OutlookStatusLine Line(OutlookMailReference mail, string text) =>
            new($"{FormatTime(mail.Time, clock)} {text}", mail);
        var notification = latestAutomatic is not null && (manufacturerIn is null || latestAutomatic.Mail.Time > manufacturerIn.Time)
            ? Line(latestAutomatic.Mail, "自動応答・受付通知（実返信に含めません）") with { Explanation = latestAutomatic.AutomaticResponseReason, IsInformational = true }
            : new OutlookStatusLine(string.Empty);
        var latest = new[] { customerIn, customerOut, manufacturerIn, manufacturerOut, unclassified }
            .Where(static mail => mail is not null)
            .MaxBy(static mail => mail!.Time);
        if (latest is null)
        {
            return new(null, null, null, null, null, latestAutomatic is null ? "NO_MAIL" : "AUTOMATIC_RESPONSE_ONLY",
                latestAutomatic is null ? new OutlookStatusLine("メールなし")
                    : Line(latestAutomatic.Mail, "自動応答・受付通知のみ（実返信は未確認）") with { Explanation = latestAutomatic.AutomaticResponseReason, IsInformational = true },
                new OutlookStatusLine(string.Empty))
            {
                ClassificationReason = caseCandidates.Any(static item => item.IsSystemNotification)
                    ? "TOYO_SALESFORCE_CASE_NOTIFICATION" : "NO_MATCHING_MAIL",
                SkippedSystemNotificationCount = caseCandidates.Count(static item => item.IsSystemNotification),
                LatestAutomaticResponse = latestAutomatic?.Mail,
                AutomaticResponseCount = automatic.Length
            };
        }

        var customerNeedsReply = customerIn is not null && (customerOut is null || customerOut.Time < customerIn.Time);
        OutlookStatusLine primary, secondary = new(string.Empty);
        string state;
        if (latest == unclassified)
        {
            var inbound = matched.First(item => item.Mail == latest).IsInbound;
            primary = Line(latest, inbound ? "受信（お客様/メーカー未確認）" : "送信済み（お客様/メーカー未確認）") with
            {
                Explanation = inbound && latest.Sender.Equals("noreply@salesforce.com", StringComparison.OrdinalIgnoreCase)
                    ? "Salesforceの代理アドレスからの受信です。案件履歴に対応するメーカー連絡先がなく、お客様/メーカーを確認できません。ダブルクリックで対象メールを確認できます。"
                    : inbound
                        ? "送信元を案件履歴の顧客・メーカー連絡先と照合できませんでした。ダブルクリックで対象メールを確認できます。"
                        : "宛先を案件履歴の顧客・メーカー連絡先と照合できませんでした。ダブルクリックで対象メールを確認できます。"
            };
            state = "UNCLASSIFIED";
        }
        else if (latest == customerOut)
        {
            var replied = customerIn is not null && customerIn.Time < latest.Time;
            primary = Line(latest, replied ? "お客様へ返信済み" : "お客様へ送信済み");
            state = replied ? "CUSTOMER_REPLIED" : "CUSTOMER_OUTBOUND";
        }
        else if (latest == manufacturerIn)
        {
            var content = manufacturerContent[latest];
            if (content.ManufacturerMessageKind == OutlookManufacturerMessageKind.ProgressOnly)
            {
                primary = Line(latest, content.ManufacturerMessageSummary) with
                {
                    IsInformational = true,
                    Explanation = "本文はメーカー内の引継ぎ・調査進捗の連絡で、追加依頼は検出されませんでした。技術回答は待ちの状態です。ダブルクリックで原文を確認できます。"
                };
                var earlier = manufacturerContent.Values.Where(item => item.ManufacturerMessageKind != OutlookManufacturerMessageKind.ProgressOnly
                        && (manufacturerOut is null || item.Mail.Time > manufacturerOut.Time))
                    .MaxBy(static item => item.Mail.Time);
                if (earlier is not null)
                    secondary = Line(earlier.Mail, "以前のメーカー受信も要確認" + (customerNeedsReply ? " / お客様未返信" : string.Empty));
                else if (customerNeedsReply) secondary = Line(customerIn!, "お客様未返信");
                state = earlier is not null ? "MANUFACTURER_NEEDS_REPLY"
                    : customerNeedsReply ? "CUSTOMER_NEEDS_REPLY" : "WAITING_MANUFACTURER_REPLY";
            }
            else
            {
                primary = Line(latest, content.ManufacturerMessageKind == OutlookManufacturerMessageKind.ActionRequested
                    ? "メーカーから追加依頼（要確認）"
                    : (manufacturerOut is not null && manufacturerOut.Time < latest.Time ? "メーカーから返信" : "メーカーから受信") + "（内容確認）");
                if (customerNeedsReply) secondary = Line(customerIn!, "お客様未返信");
                state = customerNeedsReply ? "CUSTOMER_NEEDS_REPLY" : "MANUFACTURER_NEEDS_REPLY";
            }
        }
        else if (latest == manufacturerOut)
        {
            if (customerNeedsReply)
            {
                primary = Line(customerIn!, "お客様から受信");
                var repliedToManufacturer = manufacturerIn is not null && manufacturerIn.Time < latest.Time;
                secondary = Line(latest, repliedToManufacturer
                    ? "メーカーへ返信済み / お客様未返信"
                    : matched.First(item => item.Mail == latest).IsRelayedOutbound
                        ? "メーカーへ送信済み / お客様未返信" : "メーカーへ確認済み / お客様未返信");
                state = "WAITING_MANUFACTURER_REPLY";
            }
            else
            {
                primary = Line(latest, manufacturerIn is not null ? "メーカーへ返信済み"
                    : matched.First(item => item.Mail == latest).IsRelayedOutbound ? "メーカーへ送信済み" : "メーカーへ確認済み");
                if (customerOut is not null)
                    secondary = Line(customerOut, customerIn is not null && customerIn.Time < customerOut.Time ? "お客様へ返信済み" : "お客様へ送信済み");
                state = "WAITING_MANUFACTURER_REPLY";
            }
        }
        else
        {
            primary = Line(latest, "お客様から受信");
            state = "CUSTOMER_NEEDS_REPLY";
        }

        return new(customerIn, customerOut, manufacturerIn, manufacturerOut, unclassified, state, primary, secondary)
        {
            ClassificationReason = primary.Mail is not null && reasons.TryGetValue(primary.Mail, out var reason) ? reason : "UNKNOWN",
            SkippedSystemNotificationCount = caseCandidates.Count(static item => item.IsSystemNotification),
            LatestAutomaticResponse = latestAutomatic?.Mail,
            AutomaticResponseCount = automatic.Length,
            Notification = notification
        };
    }

    private static string FormatTime(DateTime time, DateTime now)
    {
        var displayed = time.AddSeconds(30);
        return displayed.Year == now.Year
            ? displayed.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture)
            : displayed.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static OutlookCaseContacts WithGroundedManufacturerRecipients(OutlookCaseKey key,
        OutlookCaseContacts contacts, IReadOnlyList<OutlookMailCandidate> candidates)
    {
        var manufacturers = new HashSet<string>(contacts.Manufacturer, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Where(static item => !item.IsInbound && !item.IsSystemNotification && !item.IsAutomaticResponse))
        {
            var addresses = (candidate.IsRelayedOutbound ? candidate.CopyAddresses : candidate.Addresses.Concat(candidate.CopyAddresses))
                .Where(static address => !string.IsNullOrWhiteSpace(address)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Only recipients already classifiable under the existing outbound rules
            // can establish a contact, and only inside this exact case. Unknown or
            // customer/mixed recipients cannot promote an incoming portal message.
            if (addresses.Length > 0 && addresses.All(address => ClassifyAddress(address, contacts,
                    candidate.IsRelayedOutbound ? key.Product : null).Party == Party.Manufacturer))
                manufacturers.UnionWith(addresses);
        }
        return new(contacts.Customer, manufacturers);
    }

    private static PartyMatch ClassifyAddress(string address, OutlookCaseContacts contacts, string? relayedProduct, string? senderProduct = null, bool isOutbound = false)
    {
        if (contacts.Customer.Contains(address)) return new(Party.Customer, "EXACT_CUSTOMER_ADDRESS", 2);
        if (contacts.Manufacturer.Contains(address)) return new(Party.Manufacturer, "EXACT_MANUFACTURER_ADDRESS", 2);
        if (senderProduct is not null && OutlookManufacturerSupport.IsPublishedSupportAddress(senderProduct, address))
            return new(Party.Manufacturer, isOutbound ? "PUBLISHED_PRODUCT_SUPPORT_RECIPIENT" : "PUBLISHED_PRODUCT_SUPPORT_SENDER", 1);
        var at = address.LastIndexOf('@');
        if (at < 0) return new(Party.Unknown, "UNKNOWN", 0);
        var domain = address[(at + 1)..];
        if (!CanInferDomain(domain)) return new(Party.Unknown, "UNKNOWN", 0);
        var customer = contacts.Customer.Any(known => MatchesCaseDomain(known, domain));
        var manufacturer = contacts.Manufacturer.Any(known => MatchesCaseDomain(known, domain));
        if (!customer && !manufacturer && relayedProduct is not null
            && relayedProduct.Equals("Checkmarx", StringComparison.OrdinalIgnoreCase)
            && domain.Equals("checkmarx.com", StringComparison.OrdinalIgnoreCase))
            return new(Party.Manufacturer, "CHECKMARX_RELAY_RECIPIENT", 1);
        return customer == manufacturer ? new(Party.Unknown, "UNKNOWN", 0)
            : customer ? new(Party.Customer, "CASE_CUSTOMER_DOMAIN", 1)
            : new(Party.Manufacturer, "CASE_MANUFACTURER_DOMAIN", 1);
    }

    private static bool MatchesCaseDomain(string known, string domain)
    {
        var at = known.LastIndexOf('@');
        if (at < 0) return false;
        var knownDomain = known[(at + 1)..];
        return CanInferDomain(knownDomain) && (domain.Equals(knownDomain, StringComparison.OrdinalIgnoreCase)
            || domain.EndsWith("." + knownDomain, StringComparison.OrdinalIgnoreCase));
    }
}
