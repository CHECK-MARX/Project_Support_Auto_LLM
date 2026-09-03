using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Evidence;

public sealed record CurrentCaseAttachmentManifestEntry
{
    public string SessionId { get; init; } = string.Empty;
    public string LogicalFileId { get; init; } = string.Empty;
    public string RelativeLogicalPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public string DetectedType { get; init; } = string.Empty;
    public long Size { get; init; }
    public string? ContentHash { get; init; }
    public string ParseStatus { get; init; } = "UNSUPPORTED";
    public int EvidenceCount { get; init; }
    public string Warning { get; init; } = string.Empty;
}

public sealed record CurrentCaseEvidenceResult(
    string SessionId,
    IReadOnlyList<CurrentCaseAttachmentManifestEntry> Manifest,
    IReadOnlyList<SearchSource> Evidence,
    IReadOnlyList<string> Warnings);

public sealed class CurrentCaseEvidenceService
{
    public const int MaximumEvidencePerFile = 32;
    public const int MaximumTotalEvidence = 160;
    public const long MaximumFileSize = CodexCaseFileScanner.MaximumFileSize;
    public const long MaximumZipEntrySize = 20L * 1024 * 1024;
    public const long MaximumZipExpandedSize = 100L * 1024 * 1024;
    public const int MaximumZipEntries = 500;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".log", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".config",
        ".csv", ".tsv", ".md", ".markdown", ".html", ".htm", ".cs", ".vb", ".cpp", ".c", ".h", ".hpp",
        ".java", ".py", ".js", ".ts", ".sql", ".razor", ".ps1", ".bat", ".cmd", ".sh", ".properties",
    };

    private static readonly HashSet<string> SourceExtensions = new(TextExtensions, StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".cpp", ".c", ".h", ".hpp", ".java", ".py", ".js", ".ts", ".sql", ".ps1", ".sh",
    };

    public async Task<CurrentCaseEvidenceResult> BuildAsync(
        string caseFolder,
        string sessionId,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caseFolder) || string.IsNullOrWhiteSpace(sessionId) || !Directory.Exists(caseFolder))
        {
            return new CurrentCaseEvidenceResult(sessionId, [], [], ["案件フォルダが見つからないためCurrentCase Evidenceを作成できません。"]);
        }

        var scanner = new CodexCaseFileScanner();
        var scan = await scanner.ScanAsync(caseFolder, cancellationToken).ConfigureAwait(false);
        var manifest = new List<CurrentCaseAttachmentManifestEntry>();
        var evidence = new List<SearchSource>();
        var warnings = scan.Warnings.ToList();
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in scan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logicalId = CreateLogicalFileId(file.RelativePath);
            var hash = TryHash(file.FullPath);
            var entryEvidence = new List<SearchSource>();
            var status = file.CanSendToCodex ? "PARTIAL" : "UNSUPPORTED";
            var warning = file.ExclusionReason;
            try
            {
                if (!file.CanSendToCodex)
                {
                    manifest.Add(CreateManifest(sessionId, logicalId, file, hash, status, 0, warning));
                    continue;
                }

                if (hash is not null && !seenHashes.Add(hash))
                {
                    manifest.Add(CreateManifest(sessionId, logicalId, file, hash, "DUPLICATE", 0, "同一内容の重複ファイルはEvidence化しません。"));
                    continue;
                }

                entryEvidence.AddRange(await ParseFileAsync(file, sessionId, logicalId, hash, cancellationToken).ConfigureAwait(false));
                status = entryEvidence.Count > 0 ? "PARSED" : IsPdf(file) ? "OCR_REQUIRED" : "UNSUPPORTED";
                if (entryEvidence.Count == 0 && status == "OCR_REQUIRED")
                {
                    warning = "画像のみのPDFはOCR_REQUIREDです。OCRは実装していません。";
                }
            }
            catch (InvalidDataException exception)
            {
                status = IsArchive(file) ? "UNSAFE_REJECTED" : "UNREADABLE";
                warning = exception.Message;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or XmlException)
            {
                status = "UNREADABLE";
                warning = exception.Message;
            }

            evidence.AddRange(entryEvidence.Take(Math.Max(0, MaximumTotalEvidence - evidence.Count)));
            manifest.Add(CreateManifest(sessionId, logicalId, file, hash, status, entryEvidence.Count, warning));
            if (evidence.Count >= MaximumTotalEvidence)
            {
                warnings.Add("CurrentCase Evidenceの総数上限に達しました。残りはmanifestのみ保持します。");
                break;
            }
        }

        var filtered = evidence
            .Select((source, index) => (source, index, score: Score(source, query)))
            .Where(item => item.score > 0 || string.IsNullOrWhiteSpace(query))
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.index)
            .Select(item => item.source with { Score = Math.Clamp(item.score, 0, 1) })
            .ToList();
        return new CurrentCaseEvidenceResult(sessionId, manifest, filtered, warnings);
    }

    private static async Task<IReadOnlyList<SearchSource>> ParseFileAsync(
        CodexCaseFileInfo file,
        string sessionId,
        string logicalId,
        string? hash,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.FileName);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return await ParseZipAsync(file, sessionId, logicalId, hash, cancellationToken).ConfigureAwait(false);
        }

        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return await ParseXmlAsync(file, sessionId, logicalId, hash, cancellationToken).ConfigureAwait(false);
        }

        if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return await ParseXlsxAsync(file, sessionId, logicalId, hash, cancellationToken).ConfigureAwait(false);
        }

        var content = await ManualDocumentTextExtractor.ReadAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
        var text = content.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (content.Pages is { Count: > 0 })
        {
            return content.Pages
                .Where(static page => !string.IsNullOrWhiteSpace(page.Text))
                .Take(MaximumEvidencePerFile)
                .Select(page => CreateSource(
                    sessionId,
                    logicalId,
                    file,
                    hash,
                    page.Text.Trim(),
                    "PdfPage",
                    $"pdf:page:{page.PageNumber}",
                    page.PageNumber,
                    null))
                .ToList();
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var max = Math.Min(lines.Length, MaximumEvidencePerFile);
        var results = new List<SearchSource>();
        for (var index = 0; index < max; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var locator = extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? $"csv:row:{index + 1}"
                : $"line:{index + 1}";
            results.Add(CreateSource(sessionId, logicalId, file, hash, line,
                content.DocumentType, locator, pageNumber: null, entryPath: null));
        }
        return results;
    }

    private static async Task<IReadOnlyList<SearchSource>> ParseXlsxAsync(
        CodexCaseFileInfo file, string sessionId, string logicalId, string? hash, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            using var document = SpreadsheetDocument.Open(file.FullPath, false);
            var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("XLSX workbookがありません。");
            var workbook = workbookPart.Workbook ?? throw new InvalidDataException("XLSX workbookがありません。");
            var sharedStrings = workbookPart.GetPartsOfType<SharedStringTablePart>().FirstOrDefault()?.SharedStringTable;
            var results = new List<SearchSource>();
            foreach (var sheet in workbook.Sheets?.Elements<Sheet>() ?? Enumerable.Empty<Sheet>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relationshipId = sheet.Id?.Value;
                if (string.IsNullOrWhiteSpace(relationshipId)) continue;
                var part = (WorksheetPart)workbookPart.GetPartById(relationshipId);
                var worksheet = part.Worksheet ?? throw new InvalidDataException("XLSX worksheetがありません。");
                foreach (var cell in worksheet.Descendants<Cell>())
                {
                    var value = GetCellValue(cell, sharedStrings);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    results.Add(CreateSource(sessionId, logicalId, file, hash, value, "XlsxCell",
                        $"xlsx:{sheet.Name}!{cell.CellReference}", null, null));
                    if (results.Count >= MaximumEvidencePerFile) return (IReadOnlyList<SearchSource>)results;
                }
            }
            return (IReadOnlyList<SearchSource>)results;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string GetCellValue(Cell cell, SharedStringTable? sharedStrings)
    {
        var value = cell.CellValue?.Text ?? cell.InnerText;
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(value, out var index) && sharedStrings is not null)
        {
            return sharedStrings.Elements<SharedStringItem>().ElementAtOrDefault(index)?.InnerText ?? string.Empty;
        }
        return value;
    }

    private static async Task<IReadOnlyList<SearchSource>> ParseXmlAsync(
        CodexCaseFileInfo file, string sessionId, string logicalId, string? hash, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file.FullPath);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 10_000_000,
        };
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var results = new List<SearchSource>();
        foreach (var element in document.Descendants().Where(static item => !string.IsNullOrWhiteSpace(item.Value)).Take(MaximumEvidencePerFile))
        {
            var value = element.Value.Trim();
            if (value.Length == 0) continue;
            var locator = $"xml:{string.Join('/', element.AncestorsAndSelf().Reverse().Select(item => item.Name.LocalName))}";
            results.Add(CreateSource(sessionId, logicalId, file, hash, value, "Xml", locator, null, null));
        }
        return results;
    }

    private static async Task<IReadOnlyList<SearchSource>> ParseZipAsync(
        CodexCaseFileInfo file, string sessionId, string logicalId, string? hash, CancellationToken cancellationToken)
    {
        var results = new List<SearchSource>();
        long expanded = 0;
        var count = 0;
        using var archive = ZipFile.OpenRead(file.FullPath);
        foreach (var entry in archive.Entries.OrderBy(static item => item.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            count++;
            var normalized = entry.FullName.Replace('\\', '/');
            if (count > MaximumZipEntries) throw new InvalidDataException("ZIP entry数上限を超えました。");
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains("../", StringComparison.Ordinal) ||
                normalized.Contains("/..", StringComparison.Ordinal) || Path.IsPathRooted(normalized) ||
                Regex.IsMatch(normalized, "^[A-Za-z]:/", RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException($"安全でないZIP entryを拒否しました: {normalized}");
            }
            if (entry.Length < 0 || entry.Length > MaximumZipEntrySize || expanded + entry.Length > MaximumZipExpandedSize)
            {
                throw new InvalidDataException("ZIP展開後サイズ上限を超えました。");
            }
            expanded += entry.Length;
            if (!TextExtensions.Contains(Path.GetExtension(entry.Name))) continue;
            await using var input = entry.Open();
            using var memory = new MemoryStream(capacity: checked((int)entry.Length));
            await input.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(memory.ToArray());
            foreach (var item in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Select((line, index) => (Line: line.Trim(), Index: index + 1))
                .Where(item => item.Line.Length > 0)
                .Take(MaximumEvidencePerFile))
            {
                results.Add(CreateSource(sessionId, logicalId, file, hash, item.Line, SourceExtensions.Contains(Path.GetExtension(entry.Name)) ? "ZipSource" : "ZipText", $"zip:{normalized}:line:{item.Index}", null, normalized));
                if (results.Count >= MaximumEvidencePerFile) return results;
            }
        }
        return results;
    }

    private static SearchSource CreateSource(string sessionId, string logicalId, CodexCaseFileInfo file, string? hash, string text, string kind, string locator, int? pageNumber, string? entryPath) => new()
    {
        SourceId = $"current:{sessionId}:{logicalId}:{StableHash(locator)}",
        SourceType = "CurrentCase",
        Title = file.FileName,
        DocumentTitle = file.FileName,
        Text = text,
        FilePath = null,
        Score = 0,
        ProductName = null,
        DocumentId = logicalId,
        ChunkId = StableHash(locator),
        ContentHash = hash,
        CaseSessionId = sessionId,
        LogicalFileId = logicalId,
        Locator = locator,
        EvidenceKind = kind,
        PageNumber = pageNumber,
        EntryPath = entryPath,
        SectionTitle = kind,
        ParseStatus = "PARSED",
    };

    private static CurrentCaseAttachmentManifestEntry CreateManifest(string sessionId, string logicalId, CodexCaseFileInfo file, string? hash, string status, int count, string warning) => new()
    {
        SessionId = sessionId, LogicalFileId = logicalId, RelativeLogicalPath = file.RelativePath, FileName = file.FileName,
        Extension = Path.GetExtension(file.FileName), DetectedType = file.Kind.ToString(), Size = file.Size, ContentHash = hash,
        ParseStatus = status, EvidenceCount = count, Warning = warning ?? string.Empty,
    };

    private static double Score(SearchSource source, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0.5;
        var terms = query.Split([' ', '\t', '\r', '\n', '、', '。', ',', ':', '/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => item.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (terms.Length == 0) return 0.5;
        var matches = terms.Count(term => source.Text.Contains(term, StringComparison.OrdinalIgnoreCase) || source.Title.Contains(term, StringComparison.OrdinalIgnoreCase));
        return matches / (double)terms.Length;
    }

    private static string CreateLogicalFileId(string relativePath) => StableHash(relativePath.Replace('\\', '/'));
    private static string StableHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
    private static string? TryHash(string path)
    {
        try { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    private static bool IsPdf(CodexCaseFileInfo file) => Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    private static bool IsArchive(CodexCaseFileInfo file) => Path.GetExtension(file.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase);
}
