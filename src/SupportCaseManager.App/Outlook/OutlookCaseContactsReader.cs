using System.IO;
using System.Text.RegularExpressions;
using SupportCaseManager.Core.Compatibility;
using SupportCaseManager.Core.Notes;

namespace SupportCaseManager.App.Outlook;

public sealed record OutlookCustomerInquiry(string Subject, IReadOnlyList<string> BodyAnchors, DateTime ReceivedInCaseAt);

public sealed record OutlookCaseContactEvidence(
    OutlookCaseContacts Contacts,
    IReadOnlyList<OutlookCustomerInquiry> CustomerInquiries);

public static class OutlookCaseContactsReader
{
    private static readonly Regex Header = new(
        @"^(?<name>From|To|Cc|差出人|送信者|宛先)\s*[:：]\s*(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SignatureMail = new(
        @"^\s*(?:MAIL|E-Mail|Email|SMTP address|メール)\s*[:：]\s*(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Subject = new(
        @"^(?:Subject|件名)\s*[:：]\s*(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Email = new(
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static OutlookCaseContacts Read(OutlookCaseKey key) => ReadEvidence(key).Contacts;

    public static OutlookCaseContactEvidence ReadEvidence(OutlookCaseKey key)
    {
        var consultation = ReadEntries(key, "consult");
        var replies = ReadEntries(key, "reply");
        var vendor = ReadEntries(key, "vendor");
        var customers = ReadAddresses(consultation, includeFrom: true);
        customers.UnionWith(ReadAddresses(replies, includeFrom: false));
        var manufacturers = ReadAddresses(vendor, includeFrom: true);
        var ambiguous = customers.Intersect(manufacturers, StringComparer.OrdinalIgnoreCase).ToArray();
        customers.ExceptWith(ambiguous);
        manufacturers.ExceptWith(ambiguous);
        var inquiries = consultation.Select(TryReadInquiry).Where(static item => item is not null)
            .Cast<OutlookCustomerInquiry>().OrderByDescending(static item => item.ReceivedInCaseAt)
            .Take(8).ToArray();
        return new(new OutlookCaseContacts(customers, manufacturers), inquiries);
    }

    public static OutlookCaseContacts WithCustomerSenders(OutlookCaseContacts contacts, IEnumerable<string> senders)
    {
        var customers = new HashSet<string>(contacts.Customer, StringComparer.OrdinalIgnoreCase);
        var manufacturers = new HashSet<string>(contacts.Manufacturer, StringComparer.OrdinalIgnoreCase);
        foreach (var sender in senders)
        {
            var address = sender.Trim().ToLowerInvariant();
            if (address.Contains('@') && address != "noreply@salesforce.com" && !manufacturers.Contains(address))
                customers.Add(address);
        }
        return new(customers, manufacturers);
    }

    private static IReadOnlyList<CaseHistoryEntry> ReadEntries(OutlookCaseKey key, string noteKey)
    {
        var entries = new List<CaseHistoryEntry>();
        if (!Directory.Exists(key.FolderPath)) return entries;
        var definition = NoteDefinitions.GetByKey(noteKey);
        foreach (var name in definition.CandidateFileNames(key.SupportId))
        {
            var path = Path.Combine(key.FolderPath, name);
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) continue;
                entries.AddRange(CaseNoteHistoryParser.Parse(EncodingPolicy.DecodeNoteText(File.ReadAllBytes(path))));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return entries;
    }

    private static HashSet<string> ReadAddresses(IReadOnlyList<CaseHistoryEntry> entries, bool includeFrom)
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var lines = entry.Body.Split('\n');
            foreach (var line in lines.Take(12))
            {
                var match = Header.Match(line.Trim());
                if (!match.Success) continue;
                var name = match.Groups["name"].Value;
                if (name.Equals("Cc", StringComparison.OrdinalIgnoreCase))
                    AddAddresses(cc, match.Groups["value"].Value);
                else if (includeFrom || name.Equals("To", StringComparison.OrdinalIgnoreCase) || name == "宛先")
                    AddAddresses(addresses, match.Groups["value"].Value);
            }
            foreach (var line in lines)
            {
                var signature = SignatureMail.Match(line);
                if (signature.Success) AddAddresses(addresses, signature.Groups["value"].Value);
            }
        }

        foreach (var address in cc)
        {
            var domain = address[(address.LastIndexOf('@') + 1)..];
            if (OutlookCaseMailClassifier.CanInferDomain(domain)
                && addresses.Any(known => known.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase)))
                addresses.Add(address);
        }
        return addresses;
    }

    private static OutlookCustomerInquiry? TryReadInquiry(CaseHistoryEntry entry)
    {
        if (entry.Timestamp is not { } timestamp) return null;
        var lines = entry.Body.Split('\n').Select(static line => line.Trim()).ToArray();
        var subject = lines.Take(12).Select(line => Subject.Match(line)).FirstOrDefault(static match => match.Success)
            ?.Groups["value"].Value.Trim();
        if (string.IsNullOrWhiteSpace(subject) || subject.Length is < 10 or > 180) return null;
        var anchors = lines.Where(line => line.Length is >= 24 and <= 240 && !line.Contains('@')
                && !Subject.IsMatch(line))
            .Distinct(StringComparer.Ordinal).Take(8).ToArray();
        return anchors.Length == 0 ? null : new(subject, anchors, timestamp);
    }

    private static void AddAddresses(HashSet<string> addresses, string value)
    {
        foreach (Match email in Email.Matches(value))
        {
            var address = email.Value.ToLowerInvariant();
            if (!address.EndsWith("@toyo.co.jp", StringComparison.OrdinalIgnoreCase)
                && address != "noreply@salesforce.com") addresses.Add(address);
        }
    }
}
