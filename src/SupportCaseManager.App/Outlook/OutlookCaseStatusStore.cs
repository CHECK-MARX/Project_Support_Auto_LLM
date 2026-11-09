using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SupportCaseManager.App.Outlook;

// Local last-known display state only. No message body is persisted. A restored
// result is always stale until Outlook has been checked in the current process.
public sealed class OutlookCaseStatusStore(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "itoke", "SupportCaseManager", "outlook-case-status-cache-v1.json");

    private sealed record Entry(OutlookCaseKey Key, OutlookCaseMailStatus Status);
    private sealed record Snapshot(int Version, Entry[] Entries);

    public IReadOnlyDictionary<OutlookCaseKey, OutlookCaseMailStatus> Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 10 * 1024 * 1024) return new Dictionary<OutlookCaseKey, OutlookCaseMailStatus>();
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
            if (snapshot is not { Version: 1, Entries: not null }) return new Dictionary<OutlookCaseKey, OutlookCaseMailStatus>();
            return snapshot.Entries.Where(entry => entry is { Key: not null, Status: not null }
                    && !string.IsNullOrWhiteSpace(entry.Key.Product) && !string.IsNullOrWhiteSpace(entry.Key.FolderPath)
                    && entry.Key.NormalizedSupportId.Length > 0 && entry.Status.CheckedAtUtc is not null
                    && entry.Status.WorkflowState is not (null or "UNAVAILABLE")
                    && entry.Status.Primary is { Text: not null } && entry.Status.Secondary is { Text: not null }
                    && entry.Status.Notification is { Text: not null })
                .DistinctBy(static entry => entry.Key).ToDictionary(static entry => entry.Key, static entry => entry.Status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Trace.WriteLine($"Outlook status cache could not be loaded: {ex.GetType().Name}");
            return new Dictionary<OutlookCaseKey, OutlookCaseMailStatus>();
        }
    }

    public bool Save(IReadOnlyDictionary<OutlookCaseKey, OutlookCaseMailStatus> statuses)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var entries = statuses.Where(static pair => pair.Value.CheckedAtUtc is not null && pair.Value.WorkflowState != "UNAVAILABLE")
                .OrderByDescending(static pair => pair.Value.CheckedAtUtc).Take(2000)
                .Select(static pair => new Entry(pair.Key, pair.Value with { RefreshError = string.Empty })).ToArray();
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Snapshot(1, entries)));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Outlook status cache could not be saved: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
