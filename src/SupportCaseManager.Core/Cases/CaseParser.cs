using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace SupportCaseManager.Core.Cases;

public static class CaseParser
{
    private static readonly Regex FolderRegex = new("^(?<date>\\d{8})\\((?<inner>.+)\\)(?<status>.+)$", RegexOptions.Compiled);
    private static readonly Regex LegacyRegex = new("^(?<date>\\d{4,8})\\((?<inner>.+?)\\)(?<status>.+)$", RegexOptions.Compiled);
    private static readonly Regex LegacyInnerRegex = new("^(?<body>.*?)(?<digits>\\d{3,})$", RegexOptions.Compiled);

    public static CaseRecord? ParseCaseFromDirectory(DirectoryInfo directory)
    {
        var name = directory.Name;
        var match = FolderRegex.Match(name);
        var legacy = false;
        if (!match.Success)
        {
            match = LegacyRegex.Match(name);
            if (!match.Success)
            {
                return null;
            }

            legacy = true;
        }

        var created = match.Groups["date"].Value;
        var inner = match.Groups["inner"].Value.Trim();
        var statusRaw = match.Groups["status"].Value.Trim();

        var company = inner;
        var support = string.Empty;
        var underscoreIndex = inner.LastIndexOf('_');
        if (underscoreIndex >= 0)
        {
            company = inner[..underscoreIndex].Trim();
            support = inner[(underscoreIndex + 1)..].Trim();
        }
        else
        {
            var legacyInner = LegacyInnerRegex.Match(inner);
            if (legacyInner.Success)
            {
                company = legacyInner.Groups["body"].Value.Trim();
                support = legacyInner.Groups["digits"].Value.Trim();
            }
        }

        if (legacy)
        {
            created = CaseNaming.EnsureDateString(created);
        }

        var (status, stamp) = CaseNaming.SplitStatusWithLegacy(statusRaw);
        var updated = directory.LastWriteTime;
        var updatedIso = CaseNaming.ToIsoTimestamp(updated);
        if (!string.IsNullOrEmpty(stamp) && stamp.Length == 8 &&
            DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStamp))
        {
            updatedIso = CaseNaming.ToIsoTimestamp(parsedStamp);
        }

        return new CaseRecord(
            company,
            support,
            string.IsNullOrEmpty(status) ? statusRaw : status,
            created,
            directory.Name,
            directory.FullName,
            updatedIso,
            string.Empty,
            true);
    }
}
