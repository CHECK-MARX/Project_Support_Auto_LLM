using System.Security.Cryptography;
using System.Text;

namespace SupportCaseManager.Ai.Core.Artifacts;

public interface IArtifactTranslationHandler
{
    ArtifactFormat Format { get; }

    ArtifactTextTranslationPlan Extract(string filePath, CancellationToken cancellationToken = default);

    void Apply(
        string filePath,
        ArtifactTextTranslationPlan plan,
        IReadOnlyList<ArtifactTextTranslationValue> translations,
        CancellationToken cancellationToken = default);
}

public interface IArtifactTranslationService
{
    Task<ArtifactCreationPlan> CreatePlanAsync(
        ArtifactCreationRequest request,
        CancellationToken cancellationToken = default);

    Task<ArtifactCreationResult> CreateArtifactAsync(
        ArtifactCreationPlan plan,
        IReadOnlyList<ArtifactTextTranslationValue> translations,
        CancellationToken cancellationToken = default);
}

public sealed class ArtifactTranslationService : IArtifactTranslationService
{
    private readonly CaseArtifactPathPolicy pathPolicy;
    private readonly IReadOnlyDictionary<ArtifactFormat, IArtifactTranslationHandler> handlers;

    public ArtifactTranslationService(
        CaseArtifactPathPolicy? pathPolicy = null,
        IEnumerable<IArtifactTranslationHandler>? handlers = null)
    {
        this.pathPolicy = pathPolicy ?? new CaseArtifactPathPolicy();
        this.handlers = (handlers ?? [
            new WordDocumentTranslationHandler(),
            new CsvTranslationHandler(),
            new TextTranslationHandler(ArtifactFormat.PlainText),
            new TextTranslationHandler(ArtifactFormat.Markdown),
        ])
            .ToDictionary(static handler => handler.Format);
    }

    public async Task<ArtifactCreationPlan> CreatePlanAsync(
        ArtifactCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caseFolder = pathPolicy.NormalizeCaseFolder(request.CaseFolder);
        var source = pathPolicy.NormalizeSourceFile(caseFolder, request.SourceFilePath);
        var format = CaseArtifactPathPolicy.GetArtifactFormat(source);
        if (!handlers.TryGetValue(format, out var handler))
        {
            throw new InvalidOperationException("このファイル形式は現在、翻訳保存に対応していません。");
        }

        var destination = pathPolicy.NormalizeDestinationFolder(caseFolder, request.DestinationFolder);
        var output = pathPolicy.BuildOutputPath(caseFolder, source, destination, request.OutputFileName);
        ArtifactTextTranslationPlan textPlan;
        try
        {
            textPlan = await Task.Run(
                () => handler.Extract(source, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                "文字コードを安全に判定できないため、このファイル形式は翻訳保存に対応していません。",
                ex);
        }
        var warnings = textPlan.Entries
            .Count(static item => !item.ShouldTranslate) switch
        {
            > 0 => new List<string>
            {
                $"Translation-excluded text elements: {textPlan.Entries.Count(static item => !item.ShouldTranslate)}",
            },
            _ => [],
        };
        if (File.Exists(output))
        {
            warnings.Add("The output file already exists. Overwrite is disabled; select another name.");
        }

        return new ArtifactCreationPlan
        {
            Kind = format switch
            {
                ArtifactFormat.Csv => ArtifactKind.CsvEnglishTranslation,
                ArtifactFormat.WordDocument => ArtifactKind.WordEnglishTranslation,
                _ => ArtifactKind.TextEnglishTranslation,
            },
            Format = format,
            Request = request,
            CaseFolderFullPath = caseFolder,
            SourceFullPath = source,
            DestinationFullPath = destination,
            OutputFullPath = output,
            SourceSha256 = await ComputeSha256Async(source, cancellationToken).ConfigureAwait(false),
            DestinationFolderWillBeCreated = !Directory.Exists(destination),
            OverwriteAllowed = false,
            SourceWillBeModified = false,
            Text = textPlan,
            Warnings = warnings,
        };
    }

    public async Task<ArtifactCreationResult> CreateArtifactAsync(
        ArtifactCreationPlan plan,
        IReadOnlyList<ArtifactTextTranslationValue> translations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(translations);
        if (!handlers.TryGetValue(plan.Format, out var handler))
        {
            throw new InvalidOperationException("このファイル形式は現在、翻訳保存に対応していません。");
        }

        var output = pathPolicy.BuildOutputPath(
            plan.CaseFolderFullPath,
            plan.SourceFullPath,
            plan.DestinationFullPath,
            plan.Request.OutputFileName);
        if (!string.Equals(output, plan.OutputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("保存先または出力名が変更されています。計画を再確認してください。");
        }

        if (File.Exists(output))
        {
            throw new IOException("同名ファイルを上書きしません。別名または連番を選択してください。");
        }

        var currentHash = await ComputeSha256Async(plan.SourceFullPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash, plan.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("翻訳元ファイルが計画後に変更されています。計画を再確認してください。");
        }

        ValidateTranslations(plan.Text, translations);
        var destinationCreated = false;
        var temporaryPath = Path.Combine(
            plan.DestinationFullPath,
            $".{Path.GetFileNameWithoutExtension(plan.Request.OutputFileName)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(plan.Request.OutputFileName)}");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(plan.DestinationFullPath))
            {
                Directory.CreateDirectory(plan.DestinationFullPath);
                destinationCreated = true;
            }

            File.Copy(plan.SourceFullPath, temporaryPath, overwrite: false);
            handler.Apply(temporaryPath, plan.Text, translations, cancellationToken);
            File.Move(temporaryPath, output, overwrite: false);
            var unchanged = translations.Count(
                static item => string.Equals(item.SourceText, item.TranslatedText, StringComparison.Ordinal));
            return new ArtifactCreationResult
            {
                Succeeded = true,
                OutputFilePath = output,
                TranslationTargetCount = plan.Text.TranslatableCount,
                TranslatedCount = translations.Count - unchanged,
                UnchangedCount = unchanged,
                Warnings = plan.Warnings,
            };
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            if (destinationCreated)
            {
                TryDeleteEmptyDirectory(plan.DestinationFullPath);
            }

            throw;
        }
    }

    private static void ValidateTranslations(
        ArtifactTextTranslationPlan plan,
        IReadOnlyList<ArtifactTextTranslationValue> translations)
    {
        var expected = plan.Entries
            .Where(static item => item.ShouldTranslate)
            .ToDictionary(static item => item.Key, StringComparer.Ordinal);
        if (translations.Count != expected.Count)
        {
            throw new InvalidDataException(
                $"Translation count mismatch. Expected: {expected.Count}, actual: {translations.Count}");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var translation in translations)
        {
            if (!seen.Add(translation.Key)
                || !expected.TryGetValue(translation.Key, out var entry)
                || !string.Equals(entry.SourceText, translation.SourceText, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(translation.TranslatedText))
            {
                throw new InvalidDataException(
                    $"The translation result cannot be mapped safely: {translation.Key}");
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }
}

internal sealed class TextTranslationHandler : IArtifactTranslationHandler
{
    public TextTranslationHandler(ArtifactFormat format) => Format = format;

    public ArtifactFormat Format { get; }

    public ArtifactTextTranslationPlan Extract(string filePath, CancellationToken cancellationToken = default)
    {
        var snapshot = TextFileCodec.Read(filePath);
        var normalized = TextFileCodec.NormalizeLineEndings(snapshot.Content);
        var lines = normalized.Split('\n', StringSplitOptions.None);
        var entries = new List<ArtifactTextTranslationEntry>(lines.Length);
        var inCodeFence = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            var isCodeFence = trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal);
            var result = inCodeFence || isCodeFence
                ? (ShouldTranslate: false, Reason: "Markdownコードブロック")
                : ExcelTextTranslationPolicy.Evaluate(line, isFormula: false);
            entries.Add(new ArtifactTextTranslationEntry
            {
                Key = $"line:{index}",
                Location = $"行 {index + 1}",
                SourceText = line,
                ShouldTranslate = result.ShouldTranslate,
                SkipReason = result.Reason,
            });
            if (isCodeFence)
            {
                inCodeFence = !inCodeFence;
            }
        }

        return new ArtifactTextTranslationPlan
        {
            Entries = entries,
            EncodingName = snapshot.EncodingName,
            HasByteOrderMark = snapshot.HasByteOrderMark,
            NewLine = snapshot.NewLine,
        };
    }

    public void Apply(
        string filePath,
        ArtifactTextTranslationPlan plan,
        IReadOnlyList<ArtifactTextTranslationValue> translations,
        CancellationToken cancellationToken = default)
    {
        var snapshot = TextFileCodec.Read(filePath);
        var lines = TextFileCodec.NormalizeLineEndings(snapshot.Content).Split('\n', StringSplitOptions.None);
        var byKey = translations.ToDictionary(static item => item.Key, StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"line:{index}";
            if (byKey.TryGetValue(key, out var translation))
            {
                if (!string.Equals(lines[index], translation.SourceText, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The text changed after plan approval: {plan.Entries[index].Location}");
                }

                lines[index] = TextFileCodec.NormalizeLineEndings(translation.TranslatedText)
                    .Replace("\n", plan.NewLine, StringComparison.Ordinal);
            }
        }

        TextFileCodec.Write(
            filePath,
            string.Join(plan.NewLine, lines),
            plan.EncodingName,
            plan.HasByteOrderMark);
    }
}

internal sealed class CsvTranslationHandler : IArtifactTranslationHandler
{
    public ArtifactFormat Format => ArtifactFormat.Csv;

    public ArtifactTextTranslationPlan Extract(string filePath, CancellationToken cancellationToken = default)
    {
        var snapshot = TextFileCodec.Read(filePath);
        var delimiter = CsvDocument.DetectDelimiter(snapshot.Content);
        var document = CsvDocument.Parse(snapshot.Content, delimiter);
        var entries = new List<ArtifactTextTranslationEntry>();
        for (var row = 0; row < document.Rows.Count; row++)
        {
            for (var column = 0; column < document.Rows[row].Count; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = document.Rows[row][column];
                var result = ExcelTextTranslationPolicy.Evaluate(value, isFormula: false);
                entries.Add(new ArtifactTextTranslationEntry
                {
                    Key = CsvDocument.Key(row, column),
                    Location = $"行 {row + 1}, 列 {column + 1}",
                    SourceText = value,
                    ShouldTranslate = result.ShouldTranslate,
                    SkipReason = result.Reason,
                });
            }
        }

        return new ArtifactTextTranslationPlan
        {
            Entries = entries,
            EncodingName = snapshot.EncodingName,
            HasByteOrderMark = snapshot.HasByteOrderMark,
            NewLine = snapshot.NewLine,
            CsvDelimiter = delimiter,
            CsvEndsWithNewLine = snapshot.Content.EndsWith('\n') || snapshot.Content.EndsWith('\r'),
        };
    }

    public void Apply(
        string filePath,
        ArtifactTextTranslationPlan plan,
        IReadOnlyList<ArtifactTextTranslationValue> translations,
        CancellationToken cancellationToken = default)
    {
        var snapshot = TextFileCodec.Read(filePath);
        var document = CsvDocument.Parse(snapshot.Content, plan.CsvDelimiter);
        var byKey = translations.ToDictionary(static item => item.Key, StringComparer.Ordinal);
        for (var row = 0; row < document.Rows.Count; row++)
        {
            for (var column = 0; column < document.Rows[row].Count; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = CsvDocument.Key(row, column);
                if (!byKey.TryGetValue(key, out var translation))
                {
                    continue;
                }

                var current = document.Rows[row][column];
                if (!string.Equals(current, translation.SourceText, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The CSV cell changed after plan approval: {key}");
                }

                document.Rows[row][column] = translation.TranslatedText;
            }
        }

        var content = document.Serialize(plan.CsvDelimiter, plan.NewLine, plan.CsvEndsWithNewLine);
        TextFileCodec.Write(filePath, content, plan.EncodingName, plan.HasByteOrderMark);
    }
}

internal sealed record TextFileSnapshot(
    string Content,
    string EncodingName,
    bool HasByteOrderMark,
    string NewLine);

internal static class TextFileCodec
{
    public static TextFileSnapshot Read(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        Encoding encoding;
        var offset = 0;
        var hasBom = false;
        string encodingName;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            offset = 3;
            hasBom = true;
            encodingName = "utf-8";
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
            offset = 2;
            hasBom = true;
            encodingName = "utf-16-le";
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
            offset = 2;
            hasBom = true;
            encodingName = "utf-16-be";
        }
        else
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            encodingName = "utf-8";
        }

        var content = encoding.GetString(bytes, offset, bytes.Length - offset);
        var newLine = content.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : content.Contains('\r') ? "\r" : "\n";
        return new TextFileSnapshot(content, encodingName, hasBom, newLine);
    }

    public static void Write(string filePath, string content, string encodingName, bool hasBom)
    {
        Encoding encoding = encodingName switch
        {
            "utf-16-le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: hasBom),
            "utf-16-be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: hasBom),
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom),
        };
        File.WriteAllText(filePath, content, encoding);
    }

    public static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

internal sealed class CsvDocument
{
    private CsvDocument(List<List<string>> rows) => Rows = rows;

    public List<List<string>> Rows { get; }

    public static char DetectDelimiter(string content)
    {
        var candidates = new[] { ',', '\t', ';' };
        return candidates
            .Select(delimiter => (delimiter, count: CountDelimiter(content, delimiter)))
            .OrderByDescending(static item => item.count)
            .ThenBy(static item => item.delimiter == ',' ? 0 : 1)
            .First()
            .delimiter;
    }

    public static CsvDocument Parse(string content, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < content.Length && content[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            if (character == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (character == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
                if (character == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
                {
                    index++;
                }
            }
            else
            {
                field.Append(character);
            }
        }

        if (row.Count > 0 || field.Length > 0 || content.Length == 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return new CsvDocument(rows);
    }

    public string Serialize(char delimiter, string newLine, bool endsWithNewLine)
    {
        var content = string.Join(
            newLine,
            Rows.Select(row => string.Join(delimiter, row.Select(value => Escape(value, delimiter)))));
        return endsWithNewLine && content.Length > 0 ? content + newLine : content;
    }

    public static string Key(int row, int column) => $"csv:{row}:{column}";

    private static int CountDelimiter(string content, char delimiter)
    {
        var count = 0;
        var quoted = false;
        foreach (var character in content)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && character == delimiter)
            {
                count++;
            }
        }

        return count;
    }

    private static string Escape(string value, char delimiter)
    {
        if (value.IndexOfAny(new[] { delimiter, '"', '\r', '\n' }) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
