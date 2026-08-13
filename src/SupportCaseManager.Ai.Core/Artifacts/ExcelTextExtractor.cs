using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Drawing = DocumentFormat.OpenXml.Drawing;
using SpreadsheetDrawing = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace SupportCaseManager.Ai.Core.Artifacts;

public interface IExcelTextExtractor
{
    Task<ExcelTextExtractionResult> ExtractAsync(string filePath, CancellationToken cancellationToken = default);
}

public sealed partial class ExcelTextExtractor : IExcelTextExtractor
{
    public Task<ExcelTextExtractionResult> ExtractAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Extract(filePath, cancellationToken), cancellationToken);
    }

    private static ExcelTextExtractionResult Extract(string filePath, CancellationToken cancellationToken)
    {
        var entries = new List<ExcelTranslationEntry>();
        var warnings = new List<string>();
        using var document = SpreadsheetDocument.Open(filePath, false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("ExcelにWorkbookPartがありません。");
        var workbook = workbookPart.Workbook
            ?? throw new InvalidDataException("ExcelにWorkbookがありません。");
        var sheets = workbook.Sheets?.Elements<Sheet>().ToArray() ?? [];
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;

        foreach (var sheet in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sheet.Id?.Value is not { Length: > 0 } relationshipId)
            {
                continue;
            }

            if (workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                continue;
            }

            var sheetName = sheet.Name?.Value ?? "(名称なし)";
            var comments = worksheetPart.WorksheetCommentsPart?.Comments?.CommentList?
                .Elements<Comment>()
                .Select(static item => item.Reference?.Value)
                .OfType<string>()
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var worksheet = worksheetPart.Worksheet
                ?? throw new InvalidDataException($"シート「{sheetName}」にWorksheetがありません。");
            var mergedRanges = worksheet
                .Descendants<MergeCell>()
                .Select(static item => item.Reference?.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();

            if (worksheetPart.DrawingsPart?.WorksheetDrawing is { } worksheetDrawing)
            {
                var pictureCount = worksheetDrawing.Descendants<SpreadsheetDrawing.Picture>().Count();
                if (pictureCount > 0)
                {
                    warnings.Add(
                        $"シート「{sheetName}」には画像が{pictureCount}件あります。画像内に焼き付けられた文字は翻訳せず、画像をそのまま維持します。");
                }
            }

            foreach (var cell in worksheet.Descendants<Cell>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cellReference = cell.CellReference?.Value;
                if (string.IsNullOrWhiteSpace(cellReference)
                    || !TryReadString(cell, sharedStrings, out var text))
                {
                    continue;
                }

                var isFormula = cell.CellFormula is not null;
                var mergedRange = mergedRanges.FirstOrDefault(range => ContainsCell(range, cellReference));
                var (shouldTranslate, skipReason) = ExcelTextTranslationPolicy.Evaluate(text, isFormula);
                entries.Add(new ExcelTranslationEntry
                {
                    Sheet = sheetName,
                    Cell = cellReference,
                    SourceText = text,
                    IsFormula = isFormula,
                    NumberFormat = GetNumberFormat(workbookPart, cell),
                    HasComment = comments.Contains(cellReference),
                    MergedRange = mergedRange,
                    ShouldTranslate = shouldTranslate,
                    SkipReason = skipReason,
                });
            }

            AddDrawingTextEntries(entries, worksheetPart, sheetName);
            var (translateSheetName, sheetNameSkipReason) = ExcelTextTranslationPolicy.Evaluate(sheetName, isFormula: false);
            entries.Add(new ExcelTranslationEntry
            {
                TargetKind = ExcelTranslationTargetKind.SheetName,
                Sheet = sheetName,
                Cell = "@sheet-name",
                SourceText = sheetName,
                NumberFormat = "SheetName",
                ShouldTranslate = translateSheetName,
                SkipReason = sheetNameSkipReason,
            });
        }

        return new ExcelTextExtractionResult
        {
            Entries = entries,
            Warnings = warnings.Distinct().ToArray(),
            SheetCount = sheets.Length,
        };
    }

    private static void AddDrawingTextEntries(
        ICollection<ExcelTranslationEntry> entries,
        WorksheetPart worksheetPart,
        string sheetName)
    {
        var paragraphs = worksheetPart.DrawingsPart?.WorksheetDrawing?
            .Descendants<Drawing.Paragraph>()
            .ToArray() ?? [];
        for (var index = 0; index < paragraphs.Length; index++)
        {
            var sourceText = string.Concat(
                paragraphs[index].Descendants<Drawing.Text>().Select(static text => text.Text));
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                continue;
            }

            var (shouldTranslate, skipReason) = ExcelTextTranslationPolicy.Evaluate(sourceText, isFormula: false);
            entries.Add(new ExcelTranslationEntry
            {
                TargetKind = ExcelTranslationTargetKind.DrawingText,
                Sheet = sheetName,
                Cell = $"@drawing-paragraph:{index}",
                DrawingParagraphIndex = index,
                SourceText = sourceText,
                NumberFormat = "DrawingML",
                ShouldTranslate = shouldTranslate,
                SkipReason = skipReason,
            });
        }
    }

    internal static bool TryReadString(Cell cell, SharedStringTable? sharedStrings, out string text)
    {
        text = string.Empty;
        var dataType = cell.DataType?.Value;
        if (dataType == CellValues.SharedString
            && int.TryParse(cell.CellValue?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && sharedStrings is not null
            && index >= 0
            && index < sharedStrings.ChildElements.Count)
        {
            text = sharedStrings.ChildElements[index].InnerText;
            return true;
        }

        if (dataType == CellValues.InlineString)
        {
            text = cell.InlineString?.InnerText ?? string.Empty;
            return true;
        }

        if (dataType == CellValues.String)
        {
            text = cell.CellValue?.Text ?? cell.InnerText;
            return true;
        }

        return false;
    }

    private static string GetNumberFormat(WorkbookPart workbookPart, Cell cell)
    {
        if (cell.StyleIndex?.Value is not uint styleIndex)
        {
            return "General";
        }

        var formats = workbookPart.WorkbookStylesPart?.Stylesheet?.CellFormats;
        if (formats is null || styleIndex >= formats.ChildElements.Count)
        {
            return "General";
        }

        var cellFormat = formats.ChildElements[(int)styleIndex] as CellFormat;
        if (cellFormat?.NumberFormatId?.Value is not uint numberFormatId)
        {
            return "General";
        }

        var custom = workbookPart.WorkbookStylesPart?.Stylesheet?.NumberingFormats?
            .Elements<NumberingFormat>()
            .FirstOrDefault(item => item.NumberFormatId?.Value == numberFormatId)
            ?.FormatCode?.Value;
        return string.IsNullOrWhiteSpace(custom)
            ? $"BuiltIn:{numberFormatId}"
            : custom;
    }

    private static bool ContainsCell(string range, string cellReference)
    {
        var parts = range.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            return string.Equals(parts[0], cellReference, StringComparison.OrdinalIgnoreCase);
        }

        if (parts.Length != 2
            || !TryParseReference(parts[0], out var startColumn, out var startRow)
            || !TryParseReference(parts[1], out var endColumn, out var endRow)
            || !TryParseReference(cellReference, out var column, out var row))
        {
            return false;
        }

        return column >= startColumn && column <= endColumn && row >= startRow && row <= endRow;
    }

    private static bool TryParseReference(string value, out int column, out int row)
    {
        column = 0;
        row = 0;
        var match = CellReferenceRegex().Match(value.Replace("$", string.Empty, StringComparison.Ordinal));
        if (!match.Success || !int.TryParse(match.Groups["row"].Value, out row))
        {
            return false;
        }

        foreach (var character in match.Groups["column"].Value.ToUpperInvariant())
        {
            column = checked(column * 26 + character - 'A' + 1);
        }

        return true;
    }

    [GeneratedRegex(@"^(?<column>[A-Z]+)(?<row>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CellReferenceRegex();
}

internal static partial class ExcelTextTranslationPolicy
{
    public static (bool ShouldTranslate, string Reason) Evaluate(string value, bool isFormula)
    {
        if (isFormula)
        {
            return (false, "数式セル");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return (false, "空文字");
        }

        if (!JapaneseTextRegex().IsMatch(value))
        {
            return (false, "日本語を含まない文字列");
        }

        if (UrlRegex().IsMatch(value)
            || EmailRegex().IsMatch(value)
            || WindowsPathRegex().IsMatch(value))
        {
            return (false, "URL、メールアドレス、またはファイルパス");
        }

        return (true, string.Empty);
    }

    [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]", RegexOptions.CultureInvariant)]
    private static partial Regex JapaneseTextRegex();

    [GeneratedRegex(@"^\s*(?:https?|ftp)://\S+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^\s*[^@\s]+@[^@\s]+\.[^@\s]+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^\s*(?:[A-Z]:\\|\\\\)[^\r\n]+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();
}
