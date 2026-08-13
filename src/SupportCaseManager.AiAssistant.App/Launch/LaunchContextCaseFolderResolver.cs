using System.IO;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.AiAssistant.App.Launch;

public static class LaunchContextCaseFolderResolver
{
    private const int MaxDepth = 4;
    private const int MaxVisitedDirectories = 20_000;

    public static AiAssistantLaunchContext Resolve(AiAssistantLaunchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.IsNullOrWhiteSpace(context.CaseFolderPath)
            && Directory.Exists(context.CaseFolderPath))
        {
            return context;
        }

        var supportNumber = context.SupportNumber.Trim();
        if (supportNumber.Length < 4)
        {
            return context;
        }

        var candidates = FindCandidates(context, supportNumber);
        var caseFolder = candidates
            .OrderByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => candidate.LastWriteTimeUtc)
            .Select(static candidate => candidate.Path)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(caseFolder))
        {
            return context;
        }

        return context with
        {
            CaseFolderPath = caseFolder,
            NoteFilePath = ResolveNoteFilePath(context.NoteFilePath, caseFolder),
        };
    }

    private static IReadOnlyList<CaseFolderCandidate> FindCandidates(
        AiAssistantLaunchContext context,
        string supportNumber)
    {
        var candidates = new List<CaseFolderCandidate>();
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[] { context.BaseFolder, context.CloseFolder }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (!TryGetFullPath(root, out var fullRoot) || !Directory.Exists(fullRoot))
            {
                continue;
            }

            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((fullRoot, 0));
            while (queue.Count > 0 && visitedPaths.Count < MaxVisitedDirectories)
            {
                var (path, depth) = queue.Dequeue();
                if (!visitedPaths.Add(path))
                {
                    continue;
                }

                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (name.Contains(supportNumber, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(new CaseFolderCandidate(
                        path,
                        ScoreCandidate(name, context),
                        GetLastWriteTimeUtc(path)));
                }

                if (depth >= MaxDepth)
                {
                    continue;
                }

                foreach (var child in EnumerateDirectories(path))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return candidates;
    }

    private static int ScoreCandidate(string directoryName, AiAssistantLaunchContext context)
    {
        var score = 100;
        if (context.ReceptionDate is { } receptionDate
            && directoryName.Contains(receptionDate.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }

        if (!string.IsNullOrWhiteSpace(context.CompanyName)
            && directoryName.Contains(context.CompanyName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        if (!string.IsNullOrWhiteSpace(context.Status)
            && directoryName.Contains(context.Status.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        return score;
    }

    private static string ResolveNoteFilePath(string originalPath, string caseFolder)
    {
        if (!string.IsNullOrWhiteSpace(originalPath) && File.Exists(originalPath))
        {
            return originalPath;
        }

        var fileName = string.IsNullOrWhiteSpace(originalPath) ? string.Empty : Path.GetFileName(originalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var candidate = Path.Combine(caseFolder, fileName);
        return File.Exists(candidate) ? candidate : string.Empty;
    }

    private static IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path)
                .Where(static child => !IsReparsePoint(child))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryGetFullPath(string path, out string fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private static DateTime GetLastWriteTimeUtc(string path)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private sealed record CaseFolderCandidate(string Path, int Score, DateTime LastWriteTimeUtc);
}
