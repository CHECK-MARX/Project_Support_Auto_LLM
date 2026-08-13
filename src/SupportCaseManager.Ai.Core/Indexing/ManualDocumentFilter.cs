using System.Text.RegularExpressions;

namespace SupportCaseManager.Ai.Core.Indexing;

public static class ManualDocumentFilter
{
    private static readonly HashSet<string> SupportedDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".csv", ".tsv", ".html", ".htm",
        ".rst", ".adoc", ".asciidoc", ".pdf", ".docx", ".xlsx", ".pptx",
    };

    private static readonly HashSet<string> UnsupportedDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".doc", ".xls", ".ppt",
    };

    private static readonly HashSet<string> OutOfScopeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".run", ".db", ".pdb", ".bak", ".7z", ".arc", ".tbz2",
    };

    private static readonly Regex ManualNameRegex = new(
        @"(?:^|[\s_.\-])(manual|guide|installation[\s_.\-]*notes?|user[\s_.\-]*guide|reference|getting[\s_.\-]*started|installation|configuration|setup|cli[\s_.\-]*reference|command[\s_.\-]*reference)(?:$|[\s_.\-])|(?:手順書|利用手順|設定ガイド)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LogNameRegex = new(
        @"(?:^|[\s_.\-])(trace|debug|build[\s_.\-]*log|crash[\s_.\-]*dump|console[\s_.\-]*output|execution[\s_.\-]*log|run[\s_.\-]*log|log)(?:$|[\s_.\-])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PowerShellPromptRegex = new(@"^\s*PS\s+[A-Z]:\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DriveCommandPromptRegex = new(@"^\s*[A-Z]:\\[^>\r\n]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnixPromptRegex = new(@"^\s*(?:[\w.-]+@[^\s:]+(?::[^$#]+)?|(?:bash|sh|zsh))?\s*[$#]\s+\S", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExeCommandRegex = new(@"\b[\w.\-]+\.exe\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LogLineRegex = new(
        @"^\s*(?:\d{4}[-/]\d{2}[-/]\d{2}[T\s]\d{2}:\d{2}:\d{2}|\[?(?:TRACE|DEBUG|INFO|WARN|ERROR|FATAL)\]?(?:\s|:)|at\s+[\w.]+\([^)]*\))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadingRegex = new(
        @"^\s*(?:#{1,6}\s+|\d+(?:\.\d+)*[.)]?\s+|(?:概要|目的|手順|設定|インストール|トラブルシューティング|確認方法)\s*[:：]?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ConsoleMarkers =
    [
        "Windows PowerShell Copyright",
        "Copyright (C) Microsoft Corporation. All rights reserved.",
        "No framework installation found",
    ];

    public static ManualDocumentFilterResult ClassifyFile(string filePath)
    {
        var extension = NormalizeExtension(Path.GetExtension(filePath));
        if (SupportedDocumentExtensions.Contains(extension))
        {
            return new ManualDocumentFilterResult(ManualDocumentCategory.ImportCandidate, extension, string.Empty);
        }

        if (extension == ".zip")
        {
            return new ManualDocumentFilterResult(
                ManualDocumentCategory.ArchiveCandidate,
                extension,
                "ZIP entries are inspected with archive safety limits.");
        }

        if (UnsupportedDocumentExtensions.Contains(extension))
        {
            return new ManualDocumentFilterResult(
                ManualDocumentCategory.UnsupportedDocumentFormat,
                extension,
                "Legacy Office formats and image-only documents are not imported. Use PDF/DOCX/XLSX/PPTX/HTML/TXT/MD/CSV/TSV.");
        }

        if (OutOfScopeExtensions.Contains(extension))
        {
            return new ManualDocumentFilterResult(
                ManualDocumentCategory.OutOfScopeBinaryOrArchive,
                extension,
                "Binary, database, backup, executable, or unsupported archive is outside the manual search target.");
        }

        return new ManualDocumentFilterResult(
            ManualDocumentCategory.UnsupportedOther,
            extension,
            "Unsupported extension.");
    }

    public static ManualDocumentFilterResult ClassifyTextFileContent(string filePath, string text)
    {
        text ??= string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToArray();

        var manualName = ManualNameRegex.IsMatch(fileName);
        var logName = LogNameRegex.IsMatch(fileName);
        var promptLines = lines.Count(IsPromptOrCommandLine);
        var logLines = lines.Count(line => LogLineRegex.IsMatch(line));
        var headingLines = lines.Count(line => HeadingRegex.IsMatch(line));
        var narrativeLines = lines.Count(line => IsNarrativeLine(line) && !IsConsoleMarkerLine(line));
        var markerCount = ConsoleMarkers.Count(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var executableCount = ExeCommandRegex.Matches(text).Count;
        var lineCount = Math.Max(1, lines.Length);

        var scores = new ManualDocumentContentScores
        {
            ManualScore = (manualName ? 4 : 0) + Math.Min(3, headingLines) + (narrativeLines >= 2 ? 2 : narrativeLines),
            LogScore = (logName ? 3 : 0) + Math.Min(4, markerCount * 2) + RatioScore(logLines, lineCount, 5),
            CommandExampleScore = Math.Min(5, promptLines + (executableCount >= 3 ? 2 : executableCount > 0 ? 1 : 0)),
            NarrativeTextScore = RatioScore(narrativeLines, lineCount, 5),
            StructuredHeadingScore = Math.Min(5, headingLines),
        };

        var pureOutput = lines.Length > 0
            && (logLines + promptLines) >= Math.Max(2, (int)Math.Ceiling(lines.Length * 0.65))
            && narrativeLines <= 1;
        var strongLog = scores.LogScore >= 5 && scores.NarrativeTextScore <= 1;
        var explanatoryEvidence = scores.ManualScore + scores.StructuredHeadingScore + Math.Min(3, narrativeLines);
        var outputEvidence = scores.LogScore + (pureOutput ? 2 : 0);
        var exclude = (pureOutput || strongLog) && outputEvidence > explanatoryEvidence;

        var reason = BuildDecisionReason(exclude, manualName, logName, pureOutput, explanatoryEvidence, outputEvidence, scores);
        return new ManualDocumentFilterResult(
            exclude ? ManualDocumentCategory.ContentExcludedText : ManualDocumentCategory.ImportCandidate,
            NormalizeExtension(Path.GetExtension(filePath)),
            reason,
            scores);
    }

    public static string NormalizeExtension(string? extension) => string.IsNullOrWhiteSpace(extension)
        ? "(none)"
        : extension.Trim().ToLowerInvariant();

    private static bool IsPromptOrCommandLine(string line) =>
        PowerShellPromptRegex.IsMatch(line)
        || DriveCommandPromptRegex.IsMatch(line)
        || UnixPromptRegex.IsMatch(line)
        || (ExeCommandRegex.IsMatch(line) && !IsNarrativeLine(line));

    private static bool IsConsoleMarkerLine(string line) =>
        ConsoleMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsNarrativeLine(string line)
    {
        if (line.Length < 12 || LogLineRegex.IsMatch(line) || PowerShellPromptRegex.IsMatch(line) || DriveCommandPromptRegex.IsMatch(line))
        {
            return false;
        }

        return line.IndexOfAny(['。', '．', '.', '：', ':']) >= 0
            || Regex.IsMatch(line, @"[぀-ヿ一-鿿].*(?:す|ます|します|てください|です)$")
            || Regex.Matches(line, @"\b[A-Za-z]{3,}\b").Count >= 5;
    }

    private static int RatioScore(int count, int total, int max) =>
        Math.Min(max, (int)Math.Round((double)count / Math.Max(1, total) * max, MidpointRounding.AwayFromZero));

    private static string BuildDecisionReason(
        bool excluded,
        bool manualName,
        bool logName,
        bool pureOutput,
        int explanatoryEvidence,
        int outputEvidence,
        ManualDocumentContentScores scores)
    {
        var decision = excluded ? "Excluded as execution/log output" : "Imported as explanatory manual content";
        return $"{decision}. manualName={manualName}; logName={logName}; pureOutput={pureOutput}; "
            + $"explanatoryEvidence={explanatoryEvidence}; outputEvidence={outputEvidence}; "
            + $"ManualScore={scores.ManualScore}; LogScore={scores.LogScore}; CommandExampleScore={scores.CommandExampleScore}; "
            + $"NarrativeTextScore={scores.NarrativeTextScore}; StructuredHeadingScore={scores.StructuredHeadingScore}.";
    }
}

public enum ManualDocumentCategory
{
    ImportCandidate,
    ArchiveCandidate,
    ContentExcludedText,
    UnsupportedDocumentFormat,
    OutOfScopeBinaryOrArchive,
    UnsupportedOther,
}

public sealed record ManualDocumentFilterResult(
    ManualDocumentCategory Category,
    string Extension,
    string Reason,
    ManualDocumentContentScores? Scores = null);

public sealed record ManualDocumentContentScores
{
    public int ManualScore { get; init; }
    public int LogScore { get; init; }
    public int CommandExampleScore { get; init; }
    public int NarrativeTextScore { get; init; }
    public int StructuredHeadingScore { get; init; }
}
