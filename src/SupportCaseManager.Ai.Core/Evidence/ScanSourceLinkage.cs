using System.Security.Cryptography;
using System.Text;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Evidence;

public enum ScanSourceMatchKind
{
    ExactLogicalPath,
    NormalizedRelativePath,
    UniqueSuffixPath,
    UniqueFilenameOnly,
    Ambiguous,
    NotFound,
    InvalidPath,
    InvalidLine,
}

public sealed record ScanResultFinding
{
    public string FindingId { get; init; } = string.Empty;
    public string QueryName { get; init; } = string.Empty;
    public string VulnerabilityType { get; init; } = string.Empty;
    public string ReportedFile { get; init; } = string.Empty;
    public string ReportedPath { get; init; } = string.Empty;
    public int? ReportedLine { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Sink { get; init; } = string.Empty;
    public string ResultPath { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string ScanSourceFile { get; init; } = string.Empty;
    public string ScanSourceLocator { get; init; } = string.Empty;
}

public sealed record ScanSourceLinkageDecision
{
    public ScanResultFinding Finding { get; init; } = new();
    public ScanSourceMatchKind MatchKind { get; init; }
    public string LogicalEntryPath { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int? ContextStartLine { get; init; }
    public int? ContextEndLine { get; init; }
    public string? ContentHash { get; init; }
    public string? Content { get; init; }
}

public sealed record ScanSourceLinkageResult
{
    public IReadOnlyList<ScanResultFinding> Findings { get; init; } = [];
    public IReadOnlyList<SafeZipManualEntry> ZipEntries { get; init; } = [];
    public IReadOnlyList<ScanSourceLinkageDecision> Decisions { get; init; } = [];
    public IReadOnlyList<SearchSource> SourceContextEvidence { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public static class ScanResultParser
{
    private static readonly string[] FindingIdColumns = ["findingid", "issueid", "resultid", "id"];
    private static readonly string[] QueryColumns = ["query", "queryname", "check", "checkname"];
    private static readonly string[] PathColumns = ["reportedpath", "filepath", "file", "path", "reportedfile", "sourcefile"];
    private static readonly string[] SourceColumns = ["source", "sourceline", "sourcecode"];
    private static readonly string[] LineColumns = ["reportedline", "linenumber", "line", "source_line"];
    private static readonly string[] VulnerabilityColumns = ["vulnerability", "vulnerabilitytype", "type", "category"];
    private static readonly string[] SinkColumns = ["sink", "sinkfile", "sinkpath"];
    private static readonly string[] ResultPathColumns = ["resultpath", "dataflow", "trace"];
    private static readonly string[] SeverityColumns = ["severity", "priority", "risk"];

    public static IReadOnlyList<ScanResultFinding> ParseCsv(string text, string sourceFile = "scan.csv")
    {
        var rows = ParseCsvRows(text).ToList();
        if (rows.Count < 2)
        {
            return [];
        }

        var headers = rows[0].Select(NormalizeColumn).ToArray();
        if (!headers.Any(header => PathColumns.Any(name => NormalizeColumn(name) == header)) ||
            !headers.Any(header => LineColumns.Any(name => NormalizeColumn(name) == header) || FindingIdColumns.Any(name => NormalizeColumn(name) == header)))
        {
            return [];
        }

        return rows.Skip(1)
            .Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))
            .Select((row, index) => CreateFinding(headers, row, sourceFile, $"csv:row:{index + 2}"))
            .Where(static finding => !string.IsNullOrWhiteSpace(finding.ReportedPath))
            .ToList();
    }

    public static IReadOnlyList<ScanResultFinding> ParseText(string text, string sourceFile = "scan.pdf")
    {
        var findings = new List<ScanResultFinding>();
        string? path = null;
        int? line = null;
        var row = 0;
        void Flush()
        {
            if (path is null) return;
            findings.Add(new ScanResultFinding
            {
                FindingId = $"pdf-row-{row}",
                ReportedPath = path,
                ReportedFile = Path.GetFileName(path.Replace('\\', '/')),
                ReportedLine = line,
                ScanSourceFile = sourceFile,
                ScanSourceLocator = $"pdf:line:{row}",
            });
            path = null;
            line = null;
        }

        foreach (var raw in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            row++;
            var value = raw.Trim();
            if (value.Length == 0) continue;
            var separator = value.IndexOf(':');
            if (separator <= 0) continue;
            var key = NormalizeColumn(value[..separator]);
            var item = value[(separator + 1)..].Trim();
            if (PathColumns.Any(name => NormalizeColumn(name) == key))
            {
                Flush();
                path = item;
            }
            else if (LineColumns.Any(name => NormalizeColumn(name) == key) && int.TryParse(item, out var parsed))
            {
                line = parsed;
            }
        }

        Flush();

        return findings;
    }

    private static ScanResultFinding CreateFinding(IReadOnlyList<string> headers, IReadOnlyList<string> row, string sourceFile, string locator)
    {
        string Value(params string[] names)
        {
            foreach (var name in names)
            {
                var index = headers.ToList().FindIndex(header => string.Equals(header, NormalizeColumn(name), StringComparison.OrdinalIgnoreCase));
                if (index >= 0 && index < row.Count && !string.IsNullOrWhiteSpace(row[index])) return row[index].Trim();
            }
            return string.Empty;
        }

        var reportedPath = Value(PathColumns);
        var lineText = Value(LineColumns);
        return new ScanResultFinding
        {
            FindingId = Value(FindingIdColumns),
            QueryName = Value(QueryColumns),
            VulnerabilityType = Value(VulnerabilityColumns),
            ReportedPath = reportedPath,
            ReportedFile = Path.GetFileName(reportedPath.Replace('\\', '/')),
            ReportedLine = int.TryParse(lineText, out var line) ? line : null,
            Source = Value(SourceColumns),
            Sink = Value(SinkColumns),
            ResultPath = Value(ResultPathColumns),
            Severity = Value(SeverityColumns),
            ScanSourceFile = sourceFile,
            ScanSourceLocator = locator,
        };
    }

    private static IEnumerable<IReadOnlyList<string>> ParseCsvRows(string text)
    {
        var row = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        foreach (var character in text ?? string.Empty)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (character == ',' && !quoted)
            {
                row.Add(value.ToString().Trim());
                value.Clear();
            }
            else if ((character == '\n' || character == '\r') && !quoted)
            {
                if (character == '\n' || value.Length > 0 || row.Count > 0)
                {
                    row.Add(value.ToString().Trim());
                    value.Clear();
                    if (row.Count > 0) yield return row.ToArray();
                    row.Clear();
                }
            }
            else
            {
                value.Append(character);
            }
        }

        if (value.Length > 0 || row.Count > 0)
        {
            row.Add(value.ToString().Trim());
            yield return row.ToArray();
        }
    }

    private static string NormalizeColumn(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}

public sealed class ScanSourceLinker
{
    private const int ContextRadius = 10;

    public async Task<ScanSourceLinkageResult> LinkAsync(
        IReadOnlyList<ScanResultFinding> findings,
        string archivePath,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var zip = await new SafeZipManualReader().ReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
        var decisions = new List<ScanSourceLinkageDecision>();
        var evidence = new List<SearchSource>();
        foreach (var finding in findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = Resolve(finding, zip.Entries);
            decisions.Add(decision);
            if (decision.Content is null || decision.ContextStartLine is null || decision.ContextEndLine is null) continue;
            evidence.Add(CreateEvidence(decision, sessionId, archivePath));
        }

        return new ScanSourceLinkageResult
        {
            Findings = findings,
            ZipEntries = zip.Entries.Select(static entry => entry with { ArchivePath = string.Empty }).ToList(),
            Decisions = decisions,
            SourceContextEvidence = evidence,
            Warnings = zip.Warnings,
        };
    }

    public static ScanSourceLinkageDecision Resolve(ScanResultFinding finding, IReadOnlyList<SafeZipManualEntry> entries)
    {
        if (!TryNormalizeReportedPath(finding.ReportedPath, out var requested, out var requestedWasAbsolute))
        {
            return new() { Finding = finding, MatchKind = ScanSourceMatchKind.InvalidPath, Status = "INVALID_PATH" };
        }

        var matches = entries.Where(entry => IsSafeRelativePath(entry.EntryPath) && IsSourceExtension(entry.Extension)).ToList();
        var exact = requestedWasAbsolute
            ? []
            : matches.Where(entry => string.Equals(Normalize(entry.EntryPath), requested, StringComparison.OrdinalIgnoreCase)).ToList();
        var kind = ScanSourceMatchKind.ExactLogicalPath;
        var selected = exact;
        if (selected.Count == 0 && !requestedWasAbsolute)
        {
            selected = matches.Where(entry => string.Equals(Normalize(entry.EntryPath), requested.TrimStart('.', '/'), StringComparison.OrdinalIgnoreCase)).ToList();
            kind = ScanSourceMatchKind.NormalizedRelativePath;
        }
        if (selected.Count == 0)
        {
            selected = matches.Where(entry => requested.EndsWith('/' + Normalize(entry.EntryPath), StringComparison.OrdinalIgnoreCase)).ToList();
            kind = ScanSourceMatchKind.UniqueSuffixPath;
        }
        if (selected.Count == 0)
        {
            selected = matches.Where(entry => string.Equals(Path.GetFileName(Normalize(entry.EntryPath)), Path.GetFileName(requested), StringComparison.OrdinalIgnoreCase)).ToList();
            kind = ScanSourceMatchKind.UniqueFilenameOnly;
        }
        if (selected.Count != 1)
        {
            return new()
            {
                Finding = finding,
                MatchKind = selected.Count > 1 ? ScanSourceMatchKind.Ambiguous : ScanSourceMatchKind.NotFound,
                Status = selected.Count > 1 ? "AMBIGUOUS_SOURCE" : "SOURCE_NOT_FOUND",
            };
        }

        var entry = selected[0];
        if (ContainsUnreadableText(entry.Content.Text))
        {
            return new()
            {
                Finding = finding,
                MatchKind = kind,
                LogicalEntryPath = entry.EntryPath,
                Status = "UNREADABLE_TEXT",
                ContentHash = entry.Sha256,
            };
        }
        var lines = SplitLines(entry.Content.Text);
        if (finding.ReportedLine is null)
        {
            return new() { Finding = finding, MatchKind = kind, LogicalEntryPath = entry.EntryPath, Status = "MATCH_NO_LINE", ContentHash = entry.Sha256 };
        }
        if (finding.ReportedLine < 1 || finding.ReportedLine > lines.Count)
        {
            return new() { Finding = finding, MatchKind = ScanSourceMatchKind.InvalidLine, LogicalEntryPath = entry.EntryPath, Status = "INVALID_LINE", ContentHash = entry.Sha256 };
        }

        var start = Math.Max(1, finding.ReportedLine.Value - ContextRadius);
        var end = Math.Min(lines.Count, finding.ReportedLine.Value + ContextRadius);
        return new()
        {
            Finding = finding,
            MatchKind = kind,
            LogicalEntryPath = entry.EntryPath,
            Status = "MATCH",
            ContextStartLine = start,
            ContextEndLine = end,
            ContentHash = entry.Sha256,
            Content = string.Join(Environment.NewLine, lines.Skip(start - 1).Take(end - start + 1).Select((line, index) => $"{start + index}: {line}")),
        };
    }

    private static SearchSource CreateEvidence(ScanSourceLinkageDecision decision, string sessionId, string archivePath)
    {
        var finding = decision.Finding;
        var locator = $"zip:{decision.LogicalEntryPath}:lines:{decision.ContextStartLine}-{decision.ContextEndLine}:reported:{finding.ReportedLine}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}\n{locator}\n{decision.ContentHash}"))).ToLowerInvariant()[..16];
        return new SearchSource
        {
            SourceId = $"current-source:{sessionId}:{hash}",
            SourceType = "CurrentCase",
            Title = Path.GetFileName(archivePath),
            DocumentTitle = Path.GetFileName(archivePath),
            Text = decision.Content!,
            ProductName = null,
            DocumentId = $"scan:{finding.FindingId}",
            ChunkId = hash,
            ContentHash = decision.ContentHash,
            CaseSessionId = sessionId,
            LogicalFileId = hash,
            Locator = locator,
            EvidenceKind = "SourceContext",
            EntryPath = decision.LogicalEntryPath,
            SectionTitle = "SourceContext",
            ParseStatus = "PARSED",
            ScanEvidenceId = finding.FindingId,
            ReportedLine = finding.ReportedLine,
            ContextStartLine = decision.ContextStartLine,
            ContextEndLine = decision.ContextEndLine,
            SourceRole = finding.Source,
            SinkRole = finding.Sink,
            ResultPath = finding.ResultPath,
            Score = 0.5,
        };
    }

    private static IReadOnlyList<string> SplitLines(string text) => (text ?? string.Empty)
        .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static bool ContainsUnreadableText(string text) => text.Any(character =>
        character == '\uFFFD' || character == '\0' || (char.IsControl(character) && character is not '\r' and not '\n' and not '\t'));

    private static bool TryNormalizeReportedPath(string value, out string normalized, out bool wasAbsolute)
    {
        normalized = string.Empty;
        wasAbsolute = false;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0')) return false;
        var candidate = value.Trim().Trim('"').Replace('\\', '/');
        wasAbsolute = candidate.StartsWith("/", StringComparison.Ordinal) || candidate.StartsWith("//", StringComparison.Ordinal) ||
            (candidate.Length >= 2 && candidate[1] == ':');
        if (wasAbsolute)
        {
            var drive = candidate.IndexOf(":/", StringComparison.Ordinal);
            candidate = drive >= 0 ? candidate[(drive + 2)..] : candidate.TrimStart('/');
        }
        var parts = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(static part => part == "..")) return false;
        normalized = string.Join('/', parts.Where(static part => part != "."));
        return normalized.Length > 0;
    }

    private static bool TryNormalizeRelativePath(string value, out string normalized) => TryNormalizeReportedPath(value, out normalized, out var wasAbsolute) && !wasAbsolute;
    private static bool IsSafeRelativePath(string value) => TryNormalizeRelativePath(value, out _);
    private static string Normalize(string value) => value.Replace('\\', '/').Trim('/');
    private static bool IsSourceExtension(string extension) => new[]
    {
        ".asp", ".aspx", ".c", ".h", ".hpp", ".cpp", ".cxx", ".cs", ".java", ".py", ".js", ".ts", ".sql", ".ps1", ".sh",
    }.Contains(extension, StringComparer.OrdinalIgnoreCase);
}
