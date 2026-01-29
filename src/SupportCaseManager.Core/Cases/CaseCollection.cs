using System;
using System.Collections.Generic;

namespace SupportCaseManager.Core.Cases;

public static class CaseCollection
{
    public static List<CaseRecord> EnsureUniqueCases(IEnumerable<CaseRecord> cases)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSupports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<CaseRecord>();

        foreach (var item in cases)
        {
            var pathKey = item.FolderPath ?? string.Empty;
            if (!string.IsNullOrEmpty(pathKey) && !seenPaths.Add(pathKey))
            {
                continue;
            }

            var supportKey = item.NormalizedSupport ?? string.Empty;
            if (!string.IsNullOrEmpty(supportKey) && !seenSupports.Add(supportKey))
            {
                continue;
            }

            unique.Add(item);
        }

        return unique;
    }
}
