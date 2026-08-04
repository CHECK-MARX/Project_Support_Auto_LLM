using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Drawing = DocumentFormat.OpenXml.Drawing;

namespace SupportCaseManager.Ai.Core.Artifacts;

public interface IExcelTranslationService
{
    Task<ArtifactCreationPlan> CreatePlanAsync(
        ArtifactCreationRequest request,
        CancellationToken cancellationToken = default);

    Task<ArtifactCreationResult> CreateArtifactAsync(
        ArtifactCreationPlan plan,
        IReadOnlyList<ExcelTranslationValue> translations,
        CancellationToken cancellationToken = default);
}

public sealed class ExcelTranslationService : IExcelTranslationService
{
    private readonly IExcelTextExtractor extractor;
    private readonly CaseArtifactPathPolicy pathPolicy;

    public ExcelTranslationService(
        IExcelTextExtractor? extractor = null,
        CaseArtifactPathPolicy? pathPolicy = null)
    {
        this.extractor = extractor ?? new ExcelTextExtractor();
        this.pathPolicy = pathPolicy ?? new CaseArtifactPathPolicy();
    }

    public async Task<ArtifactCreationPlan> CreatePlanAsync(
        ArtifactCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caseFolder = pathPolicy.NormalizeCaseFolder(request.CaseFolder);
        var source = pathPolicy.NormalizeSourceFile(caseFolder, request.SourceFilePath);
        var destination = pathPolicy.NormalizeDestinationFolder(caseFolder, request.DestinationFolder);
        var output = pathPolicy.BuildOutputPath(caseFolder, source, destination, request.OutputFileName);
        var extraction = await extractor.ExtractAsync(source, cancellationToken).ConfigureAwait(false);
        var warnings = extraction.Warnings.ToList();
        var skippedCount = extraction.Entries.Count(static item => !item.ShouldTranslate);
        if (skippedCount > 0)
        {
            warnings.Add($"Translation-excluded Excel elements: {skippedCount}");
        }

        if (File.Exists(output))
        {
            warnings.Add("The output file already exists. Overwrite is disabled; select another name.");
        }

        return new ArtifactCreationPlan
        {
            Request = request,
            CaseFolderFullPath = caseFolder,
            SourceFullPath = source,
            DestinationFullPath = destination,
            OutputFullPath = output,
            SourceSha256 = await ComputeSha256Async(source, cancellationToken).ConfigureAwait(false),
            DestinationFolderWillBeCreated = !Directory.Exists(destination),
            OverwriteAllowed = false,
            SourceWillBeModified = false,
            Excel = new ExcelTranslationPlan { Entries = extraction.Entries },
            Warnings = warnings,
        };
    }

    public async Task<ArtifactCreationResult> CreateArtifactAsync(
        ArtifactCreationPlan plan,
        IReadOnlyList<ExcelTranslationValue> translations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(translations);
        var output = pathPolicy.BuildOutputPath(
            plan.CaseFolderFullPath,
            plan.SourceFullPath,
            plan.DestinationFullPath,
            plan.Request.OutputFileName);
        if (!string.Equals(output, plan.OutputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The destination or output name changed after plan approval. Confirm the plan again.");
        }

        if (File.Exists(output))
        {
            throw new IOException("The output file already exists. Overwrite is disabled.");
        }

        var currentHash = await ComputeSha256Async(plan.SourceFullPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash, plan.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The source workbook changed after plan approval. Confirm the plan again.");
        }

        ValidateTranslations(plan, translations);
        var destinationCreated = false;
        var temporaryPath = Path.Combine(
            plan.DestinationFullPath,
            $".{Path.GetFileNameWithoutExtension(plan.Request.OutputFileName)}.{Guid.NewGuid():N}.tmp.xlsx");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(plan.DestinationFullPath))
            {
                Directory.CreateDirectory(plan.DestinationFullPath);
                destinationCreated = true;
            }

            File.Copy(plan.SourceFullPath, temporaryPath, overwrite: false);
            ApplyTranslations(temporaryPath, plan.Excel.Entries, translations, cancellationToken);
            using (var verification = SpreadsheetDocument.Open(temporaryPath, false))
            {
                _ = verification.WorkbookPart?.Workbook
                    ?? throw new InvalidDataException("The created workbook could not be reopened.");
            }

            File.Move(temporaryPath, output, overwrite: false);
            var unchanged = translations.Count(
                item => string.Equals(item.SourceText, item.TranslatedText, StringComparison.Ordinal));
            return new ArtifactCreationResult
            {
                Succeeded = true,
                OutputFilePath = output,
                TranslationTargetCount = plan.Excel.TranslatableCount,
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
        ArtifactCreationPlan plan,
        IReadOnlyList<ExcelTranslationValue> translations)
    {
        var expected = plan.Excel.Entries
            .Where(static item => item.ShouldTranslate)
            .ToDictionary(static item => Key(item.Sheet, item.Cell), StringComparer.OrdinalIgnoreCase);
        if (translations.Count != expected.Count)
        {
            throw new InvalidDataException(
                $"Translation count mismatch. Expected: {expected.Count}, actual: {translations.Count}");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var translation in translations)
        {
            var key = Key(translation.Sheet, translation.Cell);
            if (!seen.Add(key)
                || !expected.TryGetValue(key, out var entry)
                || !string.Equals(entry.SourceText, translation.SourceText, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(translation.TranslatedText))
            {
                throw new InvalidDataException(
                    $"The translation result cannot be mapped safely: {translation.Sheet}!{translation.Cell}");
            }
        }
    }

    private static void ApplyTranslations(
        string filePath,
        IReadOnlyList<ExcelTranslationEntry> entries,
        IReadOnlyList<ExcelTranslationValue> translations,
        CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Open(filePath, true);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("WorkbookPart is missing.");
        var workbook = workbookPart.Workbook
            ?? throw new InvalidDataException("Workbook is missing.");
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var entriesByKey = entries
            .Where(static item => item.ShouldTranslate)
            .ToDictionary(static item => Key(item.Sheet, item.Cell), StringComparer.OrdinalIgnoreCase);

        foreach (var translation in translations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(translation.Sheet, translation.Cell);
            if (!entriesByKey.TryGetValue(key, out var entry))
            {
                throw new InvalidDataException(
                    $"The translation target is not present in the approved plan: {translation.Sheet}!{translation.Cell}");
            }

            if (entry.TargetKind == ExcelTranslationTargetKind.SheetName)
            {
                continue;
            }

            var sheet = FindSheet(workbook, translation.Sheet);
            if (sheet.Id?.Value is not { Length: > 0 } relationshipId
                || workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                throw new InvalidDataException($"The target worksheet is missing: {translation.Sheet}");
            }

            if (entry.TargetKind == ExcelTranslationTargetKind.DrawingText)
            {
                ApplyDrawingTextTranslation(worksheetPart, entry, translation);
                continue;
            }

            ApplyCellTranslation(worksheetPart, sharedStrings, translation);
        }

        ApplySheetNameTranslations(workbookPart, workbook, entriesByKey, translations);
        workbook.Save();
    }

    private static Sheet FindSheet(Workbook workbook, string sheetName)
    {
        return workbook.Sheets?
                   .Elements<Sheet>()
                   .FirstOrDefault(
                       item => string.Equals(item.Name?.Value, sheetName, StringComparison.Ordinal))
               ?? throw new InvalidDataException($"The target sheet is missing: {sheetName}");
    }

    private static void ApplyCellTranslation(
        WorksheetPart worksheetPart,
        SharedStringTable? sharedStrings,
        ExcelTranslationValue translation)
    {
        var worksheet = worksheetPart.Worksheet
            ?? throw new InvalidDataException($"Worksheet data is missing: {translation.Sheet}");
        var cell = worksheet.Descendants<Cell>()
            .FirstOrDefault(
                item => string.Equals(
                    item.CellReference?.Value,
                    translation.Cell,
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The target cell is missing: {translation.Sheet}!{translation.Cell}");
        if (cell.CellFormula is not null)
        {
            throw new InvalidDataException(
                $"Formula cells cannot be translated: {translation.Sheet}!{translation.Cell}");
        }

        if (!ExcelTextExtractor.TryReadString(cell, sharedStrings, out var current)
            || !string.Equals(current, translation.SourceText, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The cell text changed after plan approval: {translation.Sheet}!{translation.Cell}");
        }

        cell.DataType = CellValues.InlineString;
        cell.CellValue = null;
        cell.InlineString = new InlineString(
            new Text(translation.TranslatedText) { Space = SpaceProcessingModeValues.Preserve });
        worksheet.Save();
    }

    private static void ApplyDrawingTextTranslation(
        WorksheetPart worksheetPart,
        ExcelTranslationEntry entry,
        ExcelTranslationValue translation)
    {
        var worksheetDrawing = worksheetPart.DrawingsPart?.WorksheetDrawing
            ?? throw new InvalidDataException(
                $"The target drawing is missing: {translation.Sheet}!{translation.Cell}");
        var paragraphs = worksheetDrawing.Descendants<Drawing.Paragraph>().ToArray();
        if (entry.DrawingParagraphIndex < 0 || entry.DrawingParagraphIndex >= paragraphs.Length)
        {
            throw new InvalidDataException(
                $"The target drawing paragraph is missing: {translation.Sheet}!{translation.Cell}");
        }

        var textRuns = paragraphs[entry.DrawingParagraphIndex]
            .Descendants<Drawing.Text>()
            .ToArray();
        var currentText = string.Concat(textRuns.Select(static item => item.Text));
        if (textRuns.Length == 0
            || !string.Equals(currentText, translation.SourceText, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The drawing text changed after plan approval: {translation.Sheet}!{translation.Cell}");
        }

        textRuns[0].Text = translation.TranslatedText;
        for (var index = 1; index < textRuns.Length; index++)
        {
            textRuns[index].Text = string.Empty;
        }

        worksheetDrawing.Save();
    }

    private static void ApplySheetNameTranslations(
        WorkbookPart workbookPart,
        Workbook workbook,
        IReadOnlyDictionary<string, ExcelTranslationEntry> entriesByKey,
        IReadOnlyList<ExcelTranslationValue> translations)
    {
        var sheets = workbook.Sheets?.Elements<Sheet>().ToArray() ?? [];
        var renames = new List<(string OldName, string NewName)>();
        foreach (var translation in translations)
        {
            if (!entriesByKey.TryGetValue(Key(translation.Sheet, translation.Cell), out var entry)
                || entry.TargetKind != ExcelTranslationTargetKind.SheetName)
            {
                continue;
            }

            ValidateSheetName(translation.TranslatedText);
            _ = FindSheet(workbook, translation.Sheet);
            renames.Add((translation.Sheet, translation.TranslatedText.Trim()));
        }

        var finalNames = sheets
            .Select(sheet =>
            {
                var current = sheet.Name?.Value ?? string.Empty;
                var rename = renames.FirstOrDefault(
                    item => string.Equals(item.OldName, current, StringComparison.Ordinal));
                return string.IsNullOrEmpty(rename.NewName) ? current : rename.NewName;
            })
            .ToArray();
        if (finalNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != finalNames.Length)
        {
            throw new InvalidDataException("Translated sheet names must be unique.");
        }

        foreach (var rename in renames)
        {
            FindSheet(workbook, rename.OldName).Name = rename.NewName;
            UpdateSheetReferences(workbookPart, workbook, rename.OldName, rename.NewName);
        }
    }

    private static void ValidateSheetName(string value)
    {
        var name = value.Trim();
        if (name.Length is < 1 or > 31
            || name.IndexOfAny(new[] { '[', ']', ':', '*', '?', '/', '\\' }) >= 0
            || name.StartsWith('\'')
            || name.EndsWith('\''))
        {
            throw new InvalidDataException(
                $"Invalid translated sheet name: {value}. Use 1-31 characters without []:*?/\\.");
        }
    }

    private static void UpdateSheetReferences(
        WorkbookPart workbookPart,
        Workbook workbook,
        string oldName,
        string newName)
    {
        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
            var worksheet = worksheetPart.Worksheet;
            if (worksheet is null)
            {
                continue;
            }

            var changed = false;
            foreach (var formula in worksheet.Descendants<CellFormula>())
            {
                var updated = ReplaceSheetReference(formula.Text, oldName, newName);
                if (!string.Equals(updated, formula.Text, StringComparison.Ordinal))
                {
                    formula.Text = updated;
                    changed = true;
                }
            }

            foreach (var hyperlink in worksheet.Descendants<Hyperlink>())
            {
                if (hyperlink.Location?.Value is not { Length: > 0 } location)
                {
                    continue;
                }

                var updated = ReplaceSheetReference(location, oldName, newName);
                if (!string.Equals(updated, location, StringComparison.Ordinal))
                {
                    hyperlink.Location = updated;
                    changed = true;
                }
            }

            if (changed)
            {
                worksheet.Save();
            }
        }

        if (workbook.DefinedNames is null)
        {
            return;
        }

        foreach (var definedName in workbook.DefinedNames.Elements<DefinedName>())
        {
            definedName.Text = ReplaceSheetReference(definedName.Text, oldName, newName);
        }
    }

    private static string ReplaceSheetReference(string? value, string oldName, string newName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var escapedOld = oldName.Replace("'", "''", StringComparison.Ordinal);
        var escapedNew = newName.Replace("'", "''", StringComparison.Ordinal);
        var result = value.Replace($"'{escapedOld}'!", $"'{escapedNew}'!", StringComparison.Ordinal);
        return result.Replace($"{oldName}!", $"'{escapedNew}'!", StringComparison.Ordinal);
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string Key(string sheet, string cell) => $"{sheet}\u001f{cell}";

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
