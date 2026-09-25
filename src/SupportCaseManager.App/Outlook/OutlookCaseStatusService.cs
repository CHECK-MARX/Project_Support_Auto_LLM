using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SupportCaseManager.Core.Cases;

namespace SupportCaseManager.App.Outlook;

public interface IOutlookCaseStatusGateway
{
    IReadOnlyDictionary<OutlookCaseKey, OutlookCaseMailStatus> ReadCases(IReadOnlyList<OutlookCaseKey> cases, CancellationToken token);
    bool OpenMail(OutlookCaseKey key, OutlookMailReference reference, CancellationToken token);
}

public sealed class OutlookCaseStatusService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    private readonly IOutlookCaseStatusGateway gateway;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<OutlookCaseKey, (DateTime AtUtc, OutlookCaseMailStatus Status)> cache = new();

    public OutlookCaseStatusService(IOutlookCaseStatusGateway? gateway = null) =>
        this.gateway = gateway ?? new OutlookCaseStatusGateway();

    public async Task<IReadOnlyDictionary<OutlookCaseKey, OutlookCaseMailStatus>> RefreshAsync(
        IReadOnlyList<OutlookCaseKey> cases, bool force, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var unique = cases.Where(static item => item.NormalizedSupportId.Length > 0).Distinct().ToArray();
            var now = DateTime.UtcNow;
            var missing = unique.Where(key => force || !cache.TryGetValue(key, out var value) || now - value.AtUtc >= CacheLifetime).ToArray();
            if (missing.Length > 0)
            {
                var refreshed = await RunSta(() => gateway.ReadCases(missing, token), token);
                token.ThrowIfCancellationRequested();
                foreach (var key in missing)
                {
                    cache[key] = (DateTime.UtcNow, refreshed.TryGetValue(key, out var status)
                        ? status : OutlookCaseMailStatus.Message("Outlook状況を取得できません"));
                }
            }

            return unique.ToDictionary(key => key, key => cache[key].Status);
        }
        finally { gate.Release(); }
    }

    public Task<bool> OpenMailAsync(OutlookCaseKey key, OutlookMailReference reference, CancellationToken token = default) =>
        RunSta(() => gateway.OpenMail(key, reference, token), token);

    private static Task<T> RunSta<T>(Func<T> action, CancellationToken token)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { token.ThrowIfCancellationRequested(); source.TrySetResult(action()); }
            catch (OperationCanceledException) { source.TrySetCanceled(token); }
            catch (Exception ex) { source.TrySetException(ex); }
        }) { IsBackground = true, Name = "Classic Outlook case status" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return source.Task;
    }
}

public sealed class OutlookCaseStatusGateway : IOutlookCaseStatusGateway
{
    private const int Inbox = 6;
    private const int Sent = 5;
    private const int MaxFolders = 150;
    private const int MaxResults = 2000;
    private const int MaxInquiryResults = 100;
    private const string SmtpAddressProperty = "http://schemas.microsoft.com/mapi/proptag/0x39FE001E";
    private readonly IOutlookComGateway com;

    private sealed record MailRecipients(IReadOnlyList<string> To, IReadOnlyList<string> Cc)
    {
        public string Signature => string.Join(';', To.Concat(Cc).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));
    }

    public OutlookCaseStatusGateway(IOutlookComGateway? com = null) => this.com = com ?? new OutlookComGateway();

    public IReadOnlyDictionary<OutlookCaseKey, OutlookCaseMailStatus> ReadCases(IReadOnlyList<OutlookCaseKey> cases, CancellationToken token)
    {
        var result = new Dictionary<OutlookCaseKey, OutlookCaseMailStatus>();
        object? application = null;
        try
        {
            application = com.TryGetRunningApplication();
            if (application is null) return cases.ToDictionary(key => key, _ => OutlookCaseMailStatus.Message("Outlook未起動"));
            var evidence = cases.ToDictionary(static key => key, OutlookCaseContactsReader.ReadEvidence);
            var candidates = Search(application, cases.Select(static key => key.NormalizedSupportId).Distinct().ToArray(), evidence, token);
            var ambiguousIds = cases.GroupBy(static key => key.NormalizedSupportId)
                .Where(static group => group.Select(key => key.Product + "|" + key.FolderPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in cases)
            {
                token.ThrowIfCancellationRequested();
                result[key] = ambiguousIds.Contains(key.NormalizedSupportId)
                    ? OutlookCaseMailStatus.Message("同一サポートIDの案件が複数あります")
                    : OutlookCaseMailClassifier.Build(key,
                        OutlookCaseContactsReader.WithCustomerSenders(evidence[key].Contacts,
                            candidates.Where(candidate => candidate.IsLinkedCustomerInquiry
                                && candidate.Mail.SupportId == key.NormalizedSupportId)
                                .Select(candidate => candidate.Mail.Sender)), candidates);
                Trace.WriteLine($"OutlookStatus SupportId={key.NormalizedSupportId} "
                    + $"Workflow={result[key].WorkflowState} Reason={result[key].ClassificationReason} "
                    + $"SkippedNotifications={result[key].SkippedSystemNotificationCount}");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return cases.ToDictionary(key => key, _ => OutlookCaseMailStatus.Message("Outlook状況を取得できません"));
        }
        finally { Release(application); }
        return result;
    }

    public bool OpenMail(OutlookCaseKey key, OutlookMailReference reference, CancellationToken token)
    {
        if (!string.Equals(reference.SupportId, key.NormalizedSupportId, StringComparison.OrdinalIgnoreCase)) return false;
        object? application = null, session = null, item = null;
        try
        {
            application = com.TryGetRunningApplication();
            if (application is null) return false;
            dynamic outlook = application;
            session = outlook.GetNamespace("MAPI");
            item = TryGetItem(session, reference.EntryId, reference.StoreId);
            if (reference.BodyHash.Length > 0)
            {
                if (item is null || !IsSameMail(item, reference)) return false;
                token.ThrowIfCancellationRequested();
                dynamic linkedMail = item;
                linkedMail.Display(false);
                return true;
            }
            if (item is null || !IsSameMail(item, reference))
            {
                Release(item);
                item = null;
                var replacement = Search(application, [key.NormalizedSupportId], null, token)
                    .Select(static candidate => candidate.Mail)
                    .FirstOrDefault(mail => mail.Time == reference.Time
                        && string.Equals(mail.Subject, reference.Subject, StringComparison.Ordinal)
                        && string.Equals(mail.Sender, reference.Sender, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(mail.Recipients, reference.Recipients, StringComparison.OrdinalIgnoreCase));
                if (replacement is null) return false;
                item = TryGetItem(session, replacement.EntryId, replacement.StoreId);
                if (item is null || !IsSameMail(item, replacement)) return false;
            }

            token.ThrowIfCancellationRequested();
            dynamic mail = item;
            mail.Display(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }
        finally { Release(item); Release(session); Release(application); }
    }

    private static object? TryGetItem(object session, string entryId, string storeId)
    {
        try { dynamic ns = session; return ns.GetItemFromID(entryId, storeId); }
        catch (Exception) { return null; }
    }

    private static bool IsSameMail(object item, OutlookMailReference reference)
    {
        try
        {
            dynamic mail = item;
            var subject = (string)mail.Subject;
            var body = (string)mail.Body;
            var time = reference.Time;
            var actualTime = reference.IsInbound || reference.IsRelayedOutbound
                ? (DateTime)mail.ReceivedTime : (DateTime)mail.SentOn;
            return string.Equals(subject, reference.Subject, StringComparison.Ordinal)
                && actualTime == time
                && string.Equals(reference.Sender, reference.IsInbound || reference.IsRelayedOutbound ? (string)GetSender(mail) : string.Empty, StringComparison.OrdinalIgnoreCase)
                && string.Equals(reference.Recipients, reference.IsInbound && !reference.IsRelayedOutbound ? string.Empty : (string)GetRecipients(mail), StringComparison.OrdinalIgnoreCase)
                && (reference.BodyHash.Length > 0
                    ? string.Equals(HashBody(body), reference.BodyHash, StringComparison.Ordinal)
                    : OutlookCaseMailClassifier.ContainsExactSupportId(subject, reference.SupportId)
                        || OutlookCaseMailClassifier.ContainsExactSupportId(body, reference.SupportId));
        }
        catch (Exception) { return false; }
    }

    private static List<OutlookMailCandidate> Search(object application, IReadOnlyList<string> supportIds,
        IReadOnlyDictionary<OutlookCaseKey, OutlookCaseContactEvidence>? evidence, CancellationToken token)
    {
        var matches = new List<OutlookMailCandidate>();
        object? session = null, store = null, inbox = null, sent = null;
        try
        {
            dynamic outlook = application;
            session = outlook.GetNamespace("MAPI");
            dynamic ns = session;
            store = ns.DefaultStore;
            dynamic defaultStore = store;
            if (!(bool)defaultStore.IsInstantSearchEnabled)
                throw new InvalidOperationException("Outlookの検索インデックスが利用できません。");
            inbox = ns.GetDefaultFolder(Inbox);
            sent = ns.GetDefaultFolder(Sent);
            var currentUserAddresses = GetCurrentUserAddresses(session);
            var folderCount = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            ReadFolder(inbox, true, session, currentUserAddresses, supportIds, matches, seen, ref folderCount, token);
            ReadFolder(sent, false, session, currentUserAddresses, supportIds, matches, seen, ref folderCount, token);
            if (evidence is not null)
                ReadLinkedInquiries(inbox, session, currentUserAddresses, evidence, matches, seen, token);
            return matches;
        }
        finally { Release(sent); Release(inbox); Release(store); Release(session); }
    }

    private static void ReadLinkedInquiries(object inbox, object session, IReadOnlySet<string> currentUserAddresses,
        IReadOnlyDictionary<OutlookCaseKey, OutlookCaseContactEvidence> evidence,
        List<OutlookMailCandidate> matches, HashSet<string> seen, CancellationToken token)
    {
        dynamic folder = inbox;
        var storeId = (string)folder.StoreID;
        var linked = new List<(OutlookCaseKey Key, OutlookMailCandidate Candidate)>();
        foreach (var (key, caseEvidence) in evidence)
        {
            foreach (var inquiry in caseEvidence.CustomerInquiries)
            {
                token.ThrowIfCancellationRequested();
                var escaped = inquiry.Subject.Replace("'", "''", StringComparison.Ordinal);
                object? table = null;
                var inquiryMatches = new List<(OutlookCaseKey Key, OutlookMailCandidate Candidate)>();
                try
                {
                    table = folder.GetTable($"@SQL=\"urn:schemas:httpmail:subject\" ci_phrasematch '{escaped}'", 0);
                    dynamic rows = table;
                    var inspected = 0;
                    while (!(bool)rows.EndOfTable && inspected++ < MaxInquiryResults)
                    {
                        token.ThrowIfCancellationRequested();
                        object? row = null, item = null;
                        try
                        {
                            row = rows.GetNextRow();
                            dynamic data = row;
                            var entryId = (string)data["EntryID"];
                            item = TryGetItem(session, entryId, storeId);
                            if (item is null) continue;
                            dynamic mail = item;
                            if ((int)mail.Class != 43) continue;
                            var subject = (string)mail.Subject;
                            var body = (string)mail.Body;
                            var received = (DateTime)mail.ReceivedTime;
                            if (!string.Equals(subject.Trim(), inquiry.Subject, StringComparison.OrdinalIgnoreCase)
                                || received < inquiry.ReceivedInCaseAt.AddDays(-7)
                                || received > inquiry.ReceivedInCaseAt.AddDays(1)
                                || !inquiry.BodyAnchors.Any(anchor => body.Contains(anchor, StringComparison.Ordinal))
                                || evidence.Keys.Any(other => other.NormalizedSupportId != key.NormalizedSupportId
                                    && (OutlookCaseMailClassifier.ContainsExactSupportId(subject, other.NormalizedSupportId)
                                        || OutlookCaseMailClassifier.ContainsExactSupportId(body, other.NormalizedSupportId)))) continue;
                            var sender = GetSender(mail);
                            if (sender.Length == 0 || currentUserAddresses.Contains(sender)
                                || sender.Equals("noreply@salesforce.com", StringComparison.OrdinalIgnoreCase)
                                || caseEvidence.Contacts.Manufacturer.Contains(sender)) continue;
                            var reference = new OutlookMailReference(entryId, storeId, received, subject, sender,
                                string.Empty, key.NormalizedSupportId, true) { BodyHash = HashBody(body) };
                            inquiryMatches.Add((key, new OutlookMailCandidate(reference, true, [sender])
                            {
                                IsLinkedCustomerInquiry = true
                            }));
                        }
                        finally { Release(item); Release(row); }
                    }
                    if ((bool)rows.EndOfTable) linked.AddRange(inquiryMatches);
                }
                finally { Release(table); }
            }
        }

        var shared = linked.GroupBy(static item => item.Candidate.Mail.StoreId + ":" + item.Candidate.Mail.EntryId)
            .Where(static group => group.Select(item => item.Key.NormalizedSupportId).Distinct().Count() > 1)
            .Select(static group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var group in linked.Where(item => !shared.Contains(item.Candidate.Mail.StoreId + ":" + item.Candidate.Mail.EntryId))
                     .GroupBy(static item => (item.Key.NormalizedSupportId, item.Candidate.Mail.Subject)))
        {
            var domains = group.Select(item => item.Candidate.Mail.Sender.Split('@').Last())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (domains.Length != 1) continue;
            foreach (var (_, candidate) in group)
            {
                var id = candidate.Mail.StoreId + ":" + candidate.Mail.EntryId;
                if (seen.Add(id))
                {
                    matches.Add(candidate);
                    if (matches.Count > MaxResults) throw new InvalidOperationException("Outlook検索結果の上限を超えました。");
                }
                else
                {
                    var index = matches.FindIndex(item => item.Mail.StoreId + ":" + item.Mail.EntryId == id
                        && item.Mail.SupportId == candidate.Mail.SupportId && item.IsInbound);
                    if (index >= 0) matches[index] = matches[index] with { IsLinkedCustomerInquiry = true };
                }
            }
        }
    }

    private static string HashBody(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static void ReadFolder(object folder, bool folderInbound, object session, IReadOnlySet<string> currentUserAddresses,
        IReadOnlyList<string> ids,
        List<OutlookMailCandidate> matches, HashSet<string> seen, ref int folderCount, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (++folderCount > MaxFolders) throw new InvalidOperationException("Outlookフォルダ数の上限を超えました。");
        dynamic current = folder;
        var storeId = (string)current.StoreID;
        for (var offset = 0; offset < ids.Count; offset += 16)
        {
            var filter = BuildFilter(ids.Skip(offset).Take(16));
            object? table = null;
            try
            {
                table = current.GetTable(filter, 0);
                dynamic rows = table;
                while (!(bool)rows.EndOfTable)
                {
                    token.ThrowIfCancellationRequested();
                    object? row = null, item = null;
                    try
                    {
                        row = rows.GetNextRow();
                        dynamic data = row;
                        var entryId = (string)data["EntryID"];
                        if (seen.Contains(storeId + ":" + entryId)) continue;
                        item = TryGetItem(session, entryId, storeId);
                        if (item is null) continue;
                        dynamic mail = item;
                        if ((int)mail.Class != 43) continue;
                        var subject = (string)mail.Subject;
                        var body = (string)mail.Body;
                        var matchedIds = ids.Where(id => OutlookCaseMailClassifier.ContainsExactSupportId(subject, id)
                            || OutlookCaseMailClassifier.ContainsExactSupportId(body, id)).ToArray();
                        if (matchedIds.Length != 1) continue;
                        var actualSender = GetSender(mail);
                        seen.Add(storeId + ":" + entryId);
                        var relayedOutbound = folderInbound && OutlookCaseMailClassifier.IsSalesforceRelayedOutbound(
                            actualSender, body, currentUserAddresses);
                        var storedInbound = folderInbound && !currentUserAddresses.Contains(actualSender);
                        var inbound = storedInbound && !relayedOutbound;
                        var time = storedInbound ? (DateTime)mail.ReceivedTime : (DateTime)mail.SentOn;
                        var sender = storedInbound ? actualSender : string.Empty;
                        MailRecipients recipientAddresses = storedInbound
                            && !actualSender.Equals("noreply@salesforce.com", StringComparison.OrdinalIgnoreCase)
                            ? new MailRecipients([], []) : (MailRecipients)ReadRecipients(mail);
                        var recipients = recipientAddresses.Signature;
                        IReadOnlyList<string> addresses = inbound ? [sender] : recipientAddresses.To;
                        var copies = inbound && sender.Equals("noreply@salesforce.com", StringComparison.OrdinalIgnoreCase)
                            ? recipientAddresses.To.Concat(recipientAddresses.Cc).ToArray()
                            : recipientAddresses.Cc;
                        var notification = OutlookCaseMailClassifier.IsSalesforceCaseNotification(actualSender, subject,
                            body, matchedIds[0], recipientAddresses.To.Concat(recipientAddresses.Cc).ToArray());
                        var reference = new OutlookMailReference(entryId, storeId, time, subject, sender, recipients, matchedIds[0], inbound)
                        {
                            IsRelayedOutbound = relayedOutbound,
                            BodyHash = relayedOutbound ? HashBody(body) : string.Empty
                        };
                        matches.Add(new OutlookMailCandidate(reference, inbound, addresses)
                        {
                            CopyAddresses = copies,
                            IsRelayedOutbound = relayedOutbound,
                            IsSystemNotification = notification,
                            NotificationReason = notification ? "TOYO_SALESFORCE_CASE_NOTIFICATION" : string.Empty
                        });
                        if (matches.Count > MaxResults) throw new InvalidOperationException("Outlook検索結果の上限を超えました。");
                    }
                    finally { Release(item); Release(row); }
                }
            }
            finally { Release(table); }
        }

        object? folders = null;
        try
        {
            folders = current.Folders;
            dynamic children = folders;
            var count = (int)children.Count;
            for (var index = 1; index <= count; index++)
            {
                object? child = null;
                try { child = children.Item(index); ReadFolder(child!, folderInbound, session, currentUserAddresses, ids, matches, seen, ref folderCount, token); }
                finally { Release(child); }
            }
        }
        finally { Release(folders); }
    }

    private static string BuildFilter(IEnumerable<string> ids)
    {
        var terms = ids.Select(id => id.Replace("'", "''", StringComparison.Ordinal))
            .Select(id => $"(\"urn:schemas:httpmail:subject\" ci_phrasematch '{id}' OR \"urn:schemas:httpmail:textdescription\" ci_phrasematch '{id}')");
        return "@SQL=" + string.Join(" OR ", terms);
    }

    private static string GetSender(dynamic mail)
    {
        string address;
        try
        {
            address = (string)mail.SenderEmailAddress;
            if (address.Contains('@')) return address.ToLowerInvariant();
            object? sender = null, user = null, accessor = null;
            try
            {
                sender = mail.Sender;
                dynamic entry = sender!;
                user = entry.GetExchangeUser();
                if (user is not null) { dynamic exchange = user; return ((string)exchange.PrimarySmtpAddress).ToLowerInvariant(); }
                accessor = entry.PropertyAccessor;
                dynamic properties = accessor!;
                var smtp = (string)properties.GetProperty(SmtpAddressProperty);
                if (smtp.Contains('@')) return smtp.ToLowerInvariant();
            }
            finally { Release(accessor); Release(user); Release(sender); }
        }
        catch (Exception) { return string.Empty; }
        return address.ToLowerInvariant();
    }

    private static HashSet<string> GetCurrentUserAddresses(object session)
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object? currentUser = null, entry = null, exchange = null;
        try
        {
            dynamic ns = session;
            currentUser = ns.CurrentUser;
            dynamic user = currentUser!;
            entry = user.AddressEntry;
            dynamic addressEntry = entry!;
            addresses.Add(((string)addressEntry.Address).ToLowerInvariant());
            exchange = addressEntry.GetExchangeUser();
            if (exchange is not null)
            {
                dynamic exchangeUser = exchange;
                addresses.Add(((string)exchangeUser.PrimarySmtpAddress).ToLowerInvariant());
            }
        }
        catch (Exception) { }
        finally { Release(exchange); Release(entry); Release(currentUser); }
        return addresses;
    }

    private static string GetRecipients(dynamic mail) => ReadRecipients(mail).Signature;

    private static MailRecipients ReadRecipients(dynamic mail)
    {
        var to = new List<string>();
        var cc = new List<string>();
        object? recipients = null;
        try
        {
            recipients = mail.Recipients;
            dynamic list = recipients;
            for (var index = 1; index <= (int)list.Count; index++)
            {
                object? recipient = null, entry = null, user = null, distributionList = null, accessor = null;
                try
                {
                    recipient = list.Item(index);
                    dynamic person = recipient;
                    var recipientType = (int)person.Type;
                    if (recipientType is not (1 or 2)) continue;
                    var address = (string)person.Address;
                    if (!address.Contains('@'))
                    {
                        accessor = person.PropertyAccessor;
                        dynamic properties = accessor!;
                        try { address = (string)properties.GetProperty(SmtpAddressProperty); }
                        catch (Exception) { }
                        if (!address.Contains('@'))
                        {
                            entry = person.AddressEntry;
                            dynamic directoryEntry = entry!;
                            user = directoryEntry.GetExchangeUser();
                            if (user is not null) { dynamic exchange = user; address = (string)exchange.PrimarySmtpAddress; }
                            else
                            {
                                distributionList = directoryEntry.GetExchangeDistributionList();
                                if (distributionList is not null) { dynamic group = distributionList; address = (string)group.PrimarySmtpAddress; }
                            }
                        }
                    }
                    if (!address.Contains('@')) address = "unknown";
                    if (address.EndsWith("@toyo.co.jp", StringComparison.OrdinalIgnoreCase)) continue;
                    (recipientType == 1 ? to : cc).Add(address.ToLowerInvariant());
                }
                catch (Exception) { to.Add("unknown"); }
                finally { Release(accessor); Release(distributionList); Release(user); Release(entry); Release(recipient); }
            }
        }
        finally { Release(recipients); }
        return new(to, cc);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.ReleaseComObject(value);
    }
}
