using System.Globalization;
using SupportCaseManager.Core.Compatibility;

namespace SupportCaseManager.Core.Notes;

public sealed record CaseHistoryEntry(
    string Header,
    string Body,
    DateTime? Timestamp,
    int Index);

public static class CaseNoteHistoryParser
{
    public static IReadOnlyList<CaseHistoryEntry> Parse(string? text)
    {
        var entries = new List<CaseHistoryEntry>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return entries;
        }

        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var preHeader = new List<string>();
        var body = new List<string>();
        var currentHeader = string.Empty;
        DateTime? currentTimestamp = null;
        var hasHeader = false;
        var index = 0;

        foreach (var raw in lines)
        {
            var line = raw ?? string.Empty;
            if (line.StartsWith("*****追記部_", StringComparison.Ordinal))
            {
                if (hasHeader)
                {
                    entries.Add(new CaseHistoryEntry(currentHeader, JoinLines(body), currentTimestamp, index++));
                    body.Clear();
                }
                else if (preHeader.Count > 0)
                {
                    entries.Add(new CaseHistoryEntry(string.Empty, JoinLines(preHeader), null, index++));
                    preHeader.Clear();
                }

                currentHeader = line.TrimEnd();
                currentTimestamp = TryParseHeaderTimestamp(currentHeader, out var parsed) ? parsed : null;
                hasHeader = true;
                continue;
            }

            if (line.Trim() == "--------------------------------------------------")
            {
                continue;
            }

            (hasHeader ? body : preHeader).Add(line);
        }

        if (hasHeader)
        {
            entries.Add(new CaseHistoryEntry(currentHeader, JoinLines(body), currentTimestamp, index));
        }
        else if (preHeader.Count > 0)
        {
            entries.Add(new CaseHistoryEntry(string.Empty, JoinLines(preHeader), null, index));
        }

        return entries;
    }

    public static CaseHistoryEntry? PickLatest(IReadOnlyList<CaseHistoryEntry> entries)
    {
        var withBody = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Body)).ToList();
        if (withBody.Count == 0)
        {
            return entries
                .OrderByDescending(entry => entry.Timestamp ?? DateTime.MinValue)
                .ThenByDescending(entry => entry.Index)
                .FirstOrDefault();
        }

        return withBody
            .OrderByDescending(entry => entry.Timestamp ?? DateTime.MinValue)
            .ThenByDescending(entry => entry.Index)
            .First();
    }

    private static bool TryParseHeaderTimestamp(string header, out DateTime timestamp)
    {
        timestamp = default;
        const string marker = "追記部_";
        var startIndex = header.IndexOf(marker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return false;
        }

        startIndex += marker.Length;
        var endIndex = header.IndexOf('(', startIndex);
        var candidate = (endIndex >= 0
            ? header.Substring(startIndex, endIndex - startIndex)
            : header[startIndex..]).Trim();
        var formats = new[] { "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm" };
        return DateTime.TryParseExact(candidate, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp)
            || DateTime.TryParse(candidate, out timestamp);
    }

    private static string JoinLines(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join(EncodingPolicy.LineEnding, lines);
    }
}
