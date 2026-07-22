using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SupportCaseManager.Ai.Core.Indexing;

namespace SupportCaseManager.Ai.Core.Codex;

public sealed record CodexReadableAttachmentContent(
    string RelativePath,
    string ContentType,
    string EncodingName,
    string Content,
    bool IsTruncated);

public sealed record CodexAttachmentContentReadResult(
    IReadOnlyList<CodexReadableAttachmentContent> Contents,
    IReadOnlyList<string> Warnings);

public interface ICodexAttachmentContentReader
{
    Task<CodexAttachmentContentReadResult> ReadAsync(
        string caseFolder,
        IReadOnlyList<CodexCaseFileInfo> files,
        CancellationToken cancellationToken = default);
}

public sealed class CodexAttachmentContentReader : ICodexAttachmentContentReader
{
    public const int MaximumCharactersPerFile = 40_000;
    public const int MaximumTotalCharacters = 260_000;
    private const long MaximumTextBytes = 50L * 1024 * 1024;
    private const long MaximumZipEntryBytes = 20L * 1024 * 1024;
    private const long MaximumZipExpandedBytes = 100L * 1024 * 1024;
    private const int MaximumZipEntries = 500;

    private static readonly Regex DiagnosticLineRegex = new(
        "error|exception|fail(?:ed|ure)?|fatal|denied|permission|unauthori[sz]ed|upload|validate|timeout|http|tls|proxy",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WarningLineRegex = new(
        "warning|warn",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".log", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf",
        ".config", ".csv", ".tsv", ".md", ".markdown", ".rst", ".adoc", ".asciidoc", ".html",
        ".htm", ".cs", ".vb", ".cpp", ".c", ".h", ".hpp", ".java", ".py", ".js", ".ts",
        ".ps1", ".bat", ".cmd", ".sh", ".properties", ".sln", ".csproj", ".vcxproj", ".sql",
        ".razor", ".gitignore", ".checkmarxignored",
    };

    private static readonly HashSet<string> ExtractableDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".pptx",
    };

    static CodexAttachmentContentReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<CodexAttachmentContentReadResult> ReadAsync(
        string caseFolder,
        IReadOnlyList<CodexCaseFileInfo> files,
        CancellationToken cancellationToken = default)
    {
        if (!CodexPathPolicy.TryNormalizeRoot(caseFolder, out var root, out var rootError))
        {
            return new CodexAttachmentContentReadResult([], [rootError]);
        }

        var contents = new List<CodexReadableAttachmentContent>();
        var warnings = new List<string>();
        var remainingCharacters = MaximumTotalCharacters;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remainingCharacters <= 0)
            {
                warnings.Add("添付本文の合計上限に達したため、残りのファイルは一覧のみを送信します。");
                break;
            }

            if (!CodexPathPolicy.TryNormalizeFileWithinRoot(root, file.FullPath, out var path, out var pathError))
            {
                warnings.Add($"{file.RelativePath}: {pathError}");
                continue;
            }

            if (file.IsImageInput)
            {
                continue;
            }

            try
            {
                var readable = await ReadFileAsync(file, path, cancellationToken).ConfigureAwait(false);
                if (readable is null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(readable.Content))
                {
                    warnings.Add($"{file.RelativePath}: 読み取り可能な本文を抽出できませんでした。画像のみの文書はOCRが必要です。");
                    continue;
                }

                var content = readable.Content;
                var isTruncated = readable.IsTruncated;
                if (content.Length > remainingCharacters)
                {
                    content = content[..remainingCharacters];
                    isTruncated = true;
                }

                contents.Add(readable with { Content = content, IsTruncated = isTruncated });
                remainingCharacters -= content.Length;
                if (isTruncated)
                {
                    warnings.Add($"{file.RelativePath}: 大容量のため重要行を中心にUTF-8正規化して送信します。");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                warnings.Add($"{file.RelativePath}: 本文を読み取れませんでした ({ex.Message})");
            }
        }

        return new CodexAttachmentContentReadResult(contents, warnings);
    }

    private static async Task<CodexReadableAttachmentContent?> ReadFileAsync(
        CodexCaseFileInfo file,
        string path,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var content = await ReadZipAsync(path, cancellationToken).ConfigureAwait(false);
            var excerpt = BuildExcerpt(content, MaximumCharactersPerFile);
            return new CodexReadableAttachmentContent(file.RelativePath, "ZipTextEntries", "entry-dependent", excerpt.Text, excerpt.IsTruncated);
        }

        if (ExtractableDocumentExtensions.Contains(extension))
        {
            var document = await ManualDocumentTextExtractor.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            var excerpt = BuildExcerpt(document.Text, MaximumCharactersPerFile);
            return new CodexReadableAttachmentContent(file.RelativePath, document.DocumentType, "extracted-text", excerpt.Text, excerpt.IsTruncated);
        }

        if (!TextExtensions.Contains(extension) && file.Kind is not CodexCaseFileKind.Log and not CodexCaseFileKind.Configuration and not CodexCaseFileKind.SourceCode and not CodexCaseFileKind.CustomerInquiry)
        {
            return null;
        }

        var info = new FileInfo(path);
        if (info.Length > MaximumTextBytes)
        {
            throw new NotSupportedException("50 MBを超えるテキストは自動変換対象外です。");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var decoded = DecodeText(bytes);
        var textExcerpt = BuildExcerpt(decoded.Text, MaximumCharactersPerFile);
        return new CodexReadableAttachmentContent(file.RelativePath, file.Kind.ToString(), decoded.EncodingName, textExcerpt.Text, textExcerpt.IsTruncated);
    }

    private static async Task<string> ReadZipAsync(string path, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        long expandedBytes = 0;
        var entryCount = 0;
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries.OrderBy(static entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            entryCount++;
            if (entryCount > MaximumZipEntries)
            {
                builder.AppendLine("[ZIP entry limit reached]");
                break;
            }

            if (entry.Length < 0 || entry.Length > MaximumZipEntryBytes || expandedBytes + entry.Length > MaximumZipExpandedBytes)
            {
                builder.AppendLine($"[skipped oversized entry] {entry.FullName} ({entry.Length:N0} bytes)");
                continue;
            }

            expandedBytes += entry.Length;
            var extension = Path.GetExtension(entry.Name);
            if (!TextExtensions.Contains(extension))
            {
                builder.AppendLine($"[non-text entry] {entry.FullName} ({entry.Length:N0} bytes)");
                continue;
            }

            await using var stream = entry.Open();
            using var memory = new MemoryStream(capacity: checked((int)entry.Length));
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            var decoded = DecodeText(memory.ToArray());
            builder.AppendLine($"===== ZIP ENTRY: {entry.FullName} / encoding: {decoded.EncodingName} =====");
            builder.AppendLine(decoded.Text);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static DecodedText DecodeText(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return new DecodedText(string.Empty, "empty");
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return new DecodedText(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8 BOM");
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            return new DecodedText(Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4), "UTF-32 LE");
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            return new DecodedText(new UTF32Encoding(true, false, true).GetString(bytes, 4, bytes.Length - 4), "UTF-32 BE");
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return new DecodedText(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return new DecodedText(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");
        }

        if (LooksLikeUtf16(bytes, evenBytesAreNull: false))
        {
            return new DecodedText(new UnicodeEncoding(false, false, true).GetString(bytes), "UTF-16 LE (detected)");
        }
        if (LooksLikeUtf16(bytes, evenBytesAreNull: true))
        {
            return new DecodedText(new UnicodeEncoding(true, false, true).GetString(bytes), "UTF-16 BE (detected)");
        }

        if (TryDecode(bytes, new UTF8Encoding(false, true), out var utf8))
        {
            return new DecodedText(utf8, "UTF-8");
        }
        if (TryDecode(bytes, Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), out var shiftJis))
        {
            return new DecodedText(shiftJis, "Shift-JIS/CP932");
        }

        return new DecodedText(Encoding.GetEncoding(1252).GetString(bytes), "Windows-1252 fallback");
    }

    private static bool LooksLikeUtf16(byte[] bytes, bool evenBytesAreNull)
    {
        if (bytes.Length < 4 || bytes.Length % 2 != 0)
        {
            return false;
        }

        var pairs = Math.Min(bytes.Length / 2, 2048);
        var expectedNulls = 0;
        var oppositeNulls = 0;
        for (var index = 0; index < pairs; index++)
        {
            var even = bytes[index * 2];
            var odd = bytes[(index * 2) + 1];
            expectedNulls += (evenBytesAreNull ? even : odd) == 0 ? 1 : 0;
            oppositeNulls += (evenBytesAreNull ? odd : even) == 0 ? 1 : 0;
        }

        return expectedNulls >= pairs / 3 && oppositeNulls < pairs / 10;
    }

    private static bool TryDecode(byte[] bytes, Encoding encoding, out string text)
    {
        try
        {
            text = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static TextExcerpt BuildExcerpt(string value, int maximumCharacters)
    {
        var normalized = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length <= maximumCharacters)
        {
            return new TextExcerpt(normalized, false);
        }

        var lines = normalized.Split('\n');
        var selected = new SortedSet<int>();
        AddUntilCharacterBudget(lines, selected, 0, 6_000, forward: true);
        AddUntilCharacterBudget(lines, selected, lines.Length - 1, 6_000, forward: false);
        for (var index = 0; index < lines.Length; index++)
        {
            if (!DiagnosticLineRegex.IsMatch(lines[index]))
            {
                continue;
            }

            for (var nearby = Math.Max(0, index - 2); nearby <= Math.Min(lines.Length - 1, index + 2); nearby++)
            {
                selected.Add(nearby);
            }
        }

        var warningLines = Enumerable.Range(0, lines.Length)
            .Where(index => WarningLineRegex.IsMatch(lines[index]))
            .ToArray();
        foreach (var index in warningLines.Take(25).Concat(warningLines.TakeLast(25)).Distinct())
        {
            for (var nearby = Math.Max(0, index - 1); nearby <= Math.Min(lines.Length - 1, index + 1); nearby++)
            {
                selected.Add(nearby);
            }
        }

        var builder = new StringBuilder(maximumCharacters);
        var previous = -2;
        foreach (var index in selected)
        {
            if (index > previous + 1)
            {
                builder.AppendLine("...");
            }

            var line = $"L{index + 1}: {lines[index]}";
            if (builder.Length + line.Length + Environment.NewLine.Length > maximumCharacters)
            {
                break;
            }

            builder.AppendLine(line);
            previous = index;
        }

        return new TextExcerpt(builder.ToString().TrimEnd(), true);
    }

    private static void AddUntilCharacterBudget(
        IReadOnlyList<string> lines,
        ISet<int> selected,
        int start,
        int characterBudget,
        bool forward)
    {
        var used = 0;
        for (var index = start; index >= 0 && index < lines.Count; index += forward ? 1 : -1)
        {
            if (used + lines[index].Length > characterBudget)
            {
                break;
            }

            selected.Add(index);
            used += lines[index].Length + 1;
        }
    }

    private sealed record DecodedText(string Text, string EncodingName);
    private sealed record TextExcerpt(string Text, bool IsTruncated);
}
