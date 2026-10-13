using System.Globalization;
using System.Text.RegularExpressions;
using SupportCaseManager.Core.Logging;

namespace SupportCaseManager.App.Outlook;

public interface IOutlookAcceptanceGateway
{
    IReadOnlyList<OutlookAcceptanceCandidate> Scan(DateTime sinceUtc, CancellationToken token);
    bool Revalidate(OutlookAcceptanceCandidate candidate);
}

public sealed class OutlookAcceptanceGateway : IOutlookAcceptanceGateway
{
    private const int MaxItemsPerFolder = 5000;
    private const int MaxFolders = 150;
    private readonly IOutlookComGateway com;
    private readonly IAppLogger logger;

    public OutlookAcceptanceGateway(IOutlookComGateway? com = null, IAppLogger? logger = null)
    {
        this.com = com ?? new OutlookComGateway();
        this.logger = logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<OutlookAcceptanceCandidate> Scan(DateTime sinceUtc, CancellationToken token)
    {
        object? application = null, session = null, inbox = null, sent = null;
        try
        {
            application = com.TryGetRunningApplication();
            if (application is null) throw new InvalidOperationException("Outlook未起動");
            dynamic app = application;
            session = app.GetNamespace("MAPI");
            dynamic ns = session;
            inbox = ns.GetDefaultFolder(6);
            sent = ns.GetDefaultFolder(5);
            var matches = new List<OutlookAcceptanceCandidate>();
            var foldersVisited = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            ReadFolder(inbox, "ReceivedTime", sinceUtc, matches, seen, ref foldersVisited, token);
            ReadFolder(sent, "SentOn", sinceUtc, matches, seen, ref foldersVisited, token);
            return matches.OrderBy(static item => item.ReceivedAt).ToArray();
        }
        finally
        {
            OutlookCaseStatusGateway.Release(sent);
            OutlookCaseStatusGateway.Release(inbox);
            OutlookCaseStatusGateway.Release(session);
            OutlookCaseStatusGateway.Release(application);
        }
    }

    public bool Revalidate(OutlookAcceptanceCandidate candidate)
    {
        object? application = null, session = null, item = null;
        try
        {
            application = com.TryGetRunningApplication();
            if (application is null) return false;
            dynamic app = application;
            session = app.GetNamespace("MAPI");
            dynamic ns = session;
            item = ns.GetItemFromID(candidate.EntryId, candidate.StoreId);
            if (item is null) return false;
            var current = ReadCandidate(item, candidate.EntryId, candidate.StoreId, candidate.ReceivedAt);
            return current is not null && current.MailKey == candidate.MailKey
                && current.BodyHash == candidate.BodyHash && current.SupportId == candidate.SupportId;
        }
        catch (Exception) { return false; }
        finally
        {
            OutlookCaseStatusGateway.Release(item);
            OutlookCaseStatusGateway.Release(session);
            OutlookCaseStatusGateway.Release(application);
        }
    }

    private void ReadFolder(object folder, string dateProperty, DateTime sinceUtc,
        List<OutlookAcceptanceCandidate> matches, HashSet<string> seen, ref int foldersVisited, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (++foldersVisited > MaxFolders) throw new InvalidOperationException("Outlook受付メールのフォルダ検索上限を超えました。");
        object? items = null, filtered = null, folders = null;
        try
        {
            dynamic outlookFolder = folder;
            var storeId = (string)outlookFolder.StoreID;
            items = outlookFolder.Items;
            dynamic collection = items;
            var sinceLocal = sinceUtc.ToLocalTime().ToString("MM/dd/yyyy hh:mm tt", CultureInfo.InvariantCulture);
            filtered = collection.Restrict($"[{dateProperty}] >= '{sinceLocal}'");
            dynamic results = filtered;
            results.Sort($"[{dateProperty}]", true);
            var count = (int)results.Count;
            if (count > MaxItemsPerFolder) throw new InvalidOperationException("Outlook受付メールの検索上限を超えました。");
            for (var index = 1; index <= count; index++)
            {
                token.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = results.Item(index);
                    dynamic mail = item;
                    if ((int)mail.Class != 43) continue;
                    var entryId = (string)mail.EntryID;
                    if (!seen.Add(storeId + ":" + entryId)) continue;
                    var time = (DateTime)(dateProperty == "ReceivedTime" ? mail.ReceivedTime : mail.SentOn);
                    var candidate = ReadCandidate(item!, entryId, storeId, time);
                    if (candidate is not null && candidate.ReceivedAt.ToUniversalTime() >= sinceUtc)
                        matches.Add(candidate);
                    else if (candidate is not null)
                        logger.Info($"ACCEPT_TIME_FILTER={candidate.SupportId}:BEFORE_SCAN_WINDOW");
                }
                finally { OutlookCaseStatusGateway.Release(item); }
            }
            folders = outlookFolder.Folders;
            dynamic children = folders;
            for (var index = 1; index <= (int)children.Count; index++)
            {
                object? child = null;
                try
                {
                    child = children.Item(index);
                    ReadFolder(child!, dateProperty, sinceUtc, matches, seen, ref foldersVisited, token);
                }
                finally { OutlookCaseStatusGateway.Release(child); }
            }
        }
        finally
        {
            OutlookCaseStatusGateway.Release(folders);
            OutlookCaseStatusGateway.Release(filtered);
            OutlookCaseStatusGateway.Release(items);
        }
    }

    private OutlookAcceptanceCandidate? ReadCandidate(object item, string entryId, string storeId, DateTime time)
    {
        dynamic mail = item;
        var subject = (string)mail.Subject;
        var body = (string)mail.Body;
        if (body.Length > 1024 * 1024 || !body.Contains("で受け付けました", StringComparison.Ordinal)) return null;
        var idHint = Regex.Match(body[..Math.Min(body.Length, 1600)], @"(?<![0-9])[0-9]{8}(?![0-9])").Value;
        logger.Info($"ACCEPT_MAIL_FOUND={idHint}");
        var sender = OutlookCaseStatusGateway.GetSender(mail);
        var recipients = OutlookCaseStatusGateway.ReadRecipients(mail, includeInternal: true);
        var delegated = string.Empty;
        var internetMessageId = string.Empty;
        object? propertyAccessor = null;
        try
        {
            var delegatedName = (string)mail.SentOnBehalfOfName;
            var match = System.Text.RegularExpressions.Regex.Match(delegatedName,
                @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            delegated = match.Value;
        }
        catch (Exception) { }
        try
        {
            propertyAccessor = mail.PropertyAccessor;
            dynamic properties = propertyAccessor!;
            internetMessageId = (string)properties.GetProperty("http://schemas.microsoft.com/mapi/proptag/0x1035001F");
        }
        catch (Exception) { }
        finally { OutlookCaseStatusGateway.Release(propertyAccessor); }
        return OutlookAcceptanceMail.Parse(new(entryId, storeId, time, subject, body,
            sender, recipients.To, recipients.Cc)
        {
            DelegatedSenderSmtp = delegated,
            InternetMessageId = internetMessageId
        }, logger.Info);
    }
}
