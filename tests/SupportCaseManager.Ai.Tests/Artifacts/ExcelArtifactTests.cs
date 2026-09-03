using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SupportCaseManager.Ai.Core.Artifacts;
using SupportCaseManager.Ai.Tests.Helpers;
using A = DocumentFormat.OpenXml.Drawing;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class ExcelArtifactTests
{
    [Fact]
    public async Task ExtractAsync_ReturnsStructuredTextAndExcludesFormulaAndEnglish()
    {
        using var temp = new TempDirectory();
        var source = CreateWorkbook(temp.Path);
        var result = await new ExcelTextExtractor().ExtractAsync(source);

        Assert.Equal(2, result.SheetCount);
        var japanese = Assert.Single(result.Entries, item => item.Sheet == "問い合わせ" && item.Cell == "A1");
        Assert.True(japanese.ShouldTranslate);
        Assert.True(japanese.HasComment);
        Assert.Equal("General", japanese.NumberFormat);
        var english = Assert.Single(result.Entries, item => item.Cell == "B1");
        Assert.False(english.ShouldTranslate);
        Assert.Contains("日本語を含まない", english.SkipReason);
        var stringFormula = Assert.Single(result.Entries, item => item.Cell == "C2");
        Assert.True(stringFormula.IsFormula);
        Assert.False(stringFormula.ShouldTranslate);
        var merged = Assert.Single(result.Entries, item => item.Cell == "A3");
        Assert.Equal("A3:B3", merged.MergedRange);
        Assert.Contains(result.Entries, item => item.Sheet == "追加情報" && item.Cell == "A1" && item.ShouldTranslate);
        Assert.Contains(
            result.Entries,
            item => item.TargetKind == ExcelTranslationTargetKind.DrawingText
                && item.SourceText == "図形内の日本語"
                && item.ShouldTranslate);
        Assert.Contains(
            result.Entries,
            item => item.TargetKind == ExcelTranslationTargetKind.SheetName
                && item.SourceText == "問い合わせ"
                && item.ShouldTranslate);
    }

    [Fact]
    public async Task CreatePlanAsync_DoesNotCreateDestinationOrOutput()
    {
        using var temp = new TempDirectory();
        var source = CreateWorkbook(temp.Path);
        var destination = Path.Combine(temp.Path, "メーカー連携内容");
        var service = new ExcelTranslationService();

        var plan = await service.CreatePlanAsync(CreateRequest(temp.Path, source, destination));

        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(plan.OutputFullPath));
        Assert.False(plan.SourceWillBeModified);
        Assert.False(plan.OverwriteAllowed);
        Assert.True(plan.DestinationFolderWillBeCreated);
        Assert.True(plan.Excel.TranslatableCount >= 3);
    }

    [Fact]
    public async Task CreateArtifactAsync_PreservesOriginalFormulaStyleMergeDateAndReopens()
    {
        using var temp = new TempDirectory();
        var source = CreateWorkbook(temp.Path);
        var sourceHash = ComputeHash(source);
        var destination = Path.Combine(temp.Path, "メーカー連携内容");
        var service = new ExcelTranslationService();
        var plan = await service.CreatePlanAsync(CreateRequest(temp.Path, source, destination));
        var translations = TranslateAll(plan);

        var result = await service.CreateArtifactAsync(plan, translations);

        Assert.True(result.Succeeded);
        Assert.Equal("Inquiry_Details_EN.xlsx", Path.GetFileName(result.OutputFilePath));
        Assert.Equal(sourceHash, ComputeHash(source));
        Assert.Equal("セキュアコーディングチェック結果の評価", ReadText(source, "問い合わせ", "A1"));
        Assert.Equal("Assessment of Secure Coding Check Results", ReadText(result.OutputFilePath, "Inquiry", "A1"));
        Assert.Equal("SUM(D1:E1)", ReadFormula(source, "問い合わせ", "C1"));
        Assert.Equal("SUM(D1:E1)", ReadFormula(result.OutputFilePath, "Inquiry", "C1"));
        Assert.Equal(ReadStyleIndex(source, "問い合わせ", "A1"), ReadStyleIndex(result.OutputFilePath, "Inquiry", "A1"));
        Assert.Equal(ReadStyleIndex(source, "問い合わせ", "F1"), ReadStyleIndex(result.OutputFilePath, "Inquiry", "F1"));
        Assert.Equal(ReadCellValue(source, "問い合わせ", "F1"), ReadCellValue(result.OutputFilePath, "Inquiry", "F1"));
        Assert.Equal("A3:B3", ReadMergedRange(result.OutputFilePath, "Inquiry"));
        Assert.Equal("Japanese text inside a drawing", ReadDrawingText(result.OutputFilePath, "Inquiry"));
        using var reopened = SpreadsheetDocument.Open(result.OutputFilePath, false);
        Assert.NotNull(reopened.WorkbookPart?.Workbook);
    }

    [Fact]
    public async Task CreatePlanAsync_RejectsDestinationOutsideCaseFolder()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var source = CreateWorkbook(temp.Path);
        var service = new ExcelTranslationService();

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreatePlanAsync(CreateRequest(temp.Path, source, outside.Path)));

        Assert.Contains("案件フォルダ配下", error.Message);
    }

    [Fact]
    public async Task CreateArtifactAsync_DoesNotOverwriteExistingOutput()
    {
        using var temp = new TempDirectory();
        var source = CreateWorkbook(temp.Path);
        var destination = Path.Combine(temp.Path, "メーカー連携内容");
        Directory.CreateDirectory(destination);
        var existing = Path.Combine(destination, "Inquiry_Details_EN.xlsx");
        await File.WriteAllTextAsync(existing, "existing");
        var service = new ExcelTranslationService();
        var plan = await service.CreatePlanAsync(CreateRequest(temp.Path, source, destination));

        await Assert.ThrowsAsync<IOException>(() => service.CreateArtifactAsync(plan, TranslateAll(plan)));

        Assert.Equal("existing", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public void TranslationParser_RejectsInvalidOrMismatchedJson()
    {
        var expected = new[]
        {
            new ExcelTranslationEntry
            {
                Sheet = "Sheet1",
                Cell = "A1",
                SourceText = "日本語",
                ShouldTranslate = true,
            },
        };
        var parser = new ExcelTranslationJsonParser();

        var invalid = parser.Parse("not json", expected);
        var mismatched = parser.Parse(
            """[{"sheet":"Sheet1","cell":"A1","sourceText":"別の原文","translatedText":"English"}]""",
            expected);

        Assert.False(invalid.Succeeded);
        Assert.False(mismatched.Succeeded);
        Assert.Contains(mismatched.Errors, item => item.Contains("原文が一致", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateArtifactAsync_RemovesTemporaryFileWhenApplyFails()
    {
        using var temp = new TempDirectory();
        var source = CreateWorkbook(temp.Path);
        var destination = Path.Combine(temp.Path, "メーカー連携内容");
        var service = new ExcelTranslationService();
        var validPlan = await service.CreatePlanAsync(CreateRequest(temp.Path, source, destination));
        var brokenEntry = new ExcelTranslationEntry
        {
            Sheet = "存在しないシート",
            Cell = "A1",
            SourceText = "日本語",
            ShouldTranslate = true,
        };
        var brokenPlan = validPlan with
        {
            Excel = new ExcelTranslationPlan { Entries = [brokenEntry] },
        };
        var translation = new ExcelTranslationValue
        {
            Sheet = brokenEntry.Sheet,
            Cell = brokenEntry.Cell,
            SourceText = brokenEntry.SourceText,
            TranslatedText = "English",
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.CreateArtifactAsync(brokenPlan, [translation]));

        Assert.False(File.Exists(brokenPlan.OutputFullPath));
        Assert.False(Directory.Exists(destination)
            && Directory.EnumerateFiles(destination, "*.tmp.xlsx", SearchOption.TopDirectoryOnly).Any());
    }

    [Fact]
    public void RequestDetector_RequiresExplicitExcelTranslationAndCreationTerms()
    {
        var detector = new ArtifactRequestDetector();

        Assert.True(detector.IsExplicitExcelTranslationRequest(
            "問い合わせ内容.xlsxを英語に翻訳して別名で保存してください"));
        Assert.False(detector.IsExplicitExcelTranslationRequest("問い合わせ内容.xlsxを確認してください"));
        Assert.Equal(
            "問い合わせ内容.xlsx",
            detector.FindMentionedExcelFileName("添付の問い合わせ内容.xlsxを英訳して保存"));
    }

    [Fact]
    public void PathPolicy_SuggestsUnusedNumberedNameWithoutOverwrite()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "Inquiry_Details_EN.xlsx"), "one");
        File.WriteAllText(Path.Combine(temp.Path, "Inquiry_Details_EN_2.xlsx"), "two");

        var suggested = new CaseArtifactPathPolicy()
            .SuggestNumberedFileName(temp.Path, "Inquiry_Details_EN.xlsx");

        Assert.Equal("Inquiry_Details_EN_3.xlsx", suggested);
    }

    [Fact]
    public void ArtifactPromptComposer_RequiresJsonAndBuildsUnsentManufacturerMail()
    {
        var entry = new ExcelTranslationEntry
        {
            Sheet = "Sheet1",
            Cell = "A1",
            SourceText = "Path Traversalの確認",
            ShouldTranslate = true,
        };
        var plan = new ArtifactCreationPlan
        {
            Request = new ArtifactCreationRequest { OutputFileName = "Inquiry_Details_EN.xlsx" },
            OutputFullPath = "Inquiry_Details_EN.xlsx",
            Excel = new ExcelTranslationPlan { Entries = [entry] },
        };
        var context = new ArtifactPromptContext
        {
            ProductName = "Checkmarx",
            SupportId = "00018290",
            InquiryText = "確認依頼",
            UserInstruction = "英訳してメーカーへ確認",
            CurrentCaseEvidenceReferences = "- EvidenceId: current:session:file:1\n  File: attachment.pdf\n  Locator: page:3\n  Kind: PdfPage\n  ContentHash: abc123\n  Excerpt: Sanitizer evidence",
        };
        var composer = new ArtifactPromptComposer();

        var translationPrompt = composer.ComposeTranslationPrompt(plan, [entry], context);
        var mailPrompt = composer.ComposeManufacturerMailPrompt(
            plan,
            [
                new ExcelTranslationValue
                {
                    Sheet = entry.Sheet,
                    Cell = entry.Cell,
                    SourceText = entry.SourceText,
                    TranslatedText = "Review of Path Traversal",
                },
            ],
            context,
            ["Inquiry_Details_EN.xlsx"]);

        Assert.Contains("JSON配列だけ", translationPrompt);
        Assert.Contains("\"targetKind\":\"Cell\"", translationPrompt);
        Assert.Contains("Path Traversal", translationPrompt);
        Assert.Contains("ファイル操作、シェル操作、保存、名称変更は行わない", translationPrompt);
        Assert.Contains("Hello Support Team,", mailPrompt);
        Assert.Contains("Best regards,", mailPrompt);
        Assert.Contains("自動送信はしません", mailPrompt);
        Assert.Contains("CurrentCase Evidence", mailPrompt);
        Assert.Contains("attachment.pdf", mailPrompt);
        Assert.Contains("page:3", mailPrompt);
    }

    [Fact]
    public async Task RealCaseSmoke_ReadsConfiguredWorkbookWithoutWriting()
    {
        var source = Environment.GetEnvironmentVariable("SUPPORT_CASE_ARTIFACT_SMOKE_XLSX");
        var caseFolder = Environment.GetEnvironmentVariable("SUPPORT_CASE_ARTIFACT_SMOKE_CASE");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(caseFolder))
        {
            return;
        }

        var destination = Path.Combine(caseFolder, "メーカー連携内容_成果物テスト予定");
        var plan = await new ExcelTranslationService().CreatePlanAsync(
            CreateRequest(caseFolder, source, destination));

        Console.WriteLine(
            $"Real workbook read-only plan: text={plan.Excel.Entries.Count}, translatable={plan.Excel.TranslatableCount}, warnings={plan.Warnings.Count}");
        foreach (var warning in plan.Warnings)
        {
            Console.WriteLine($"Real workbook warning: {warning}");
        }
        Assert.True(plan.Excel.Entries.Count > 0);
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(plan.OutputFullPath));
    }

    [Fact]
    public async Task RealCaseCreatedArtifact_PreservesWorkbookStructureAndTranslatesTargets()
    {
        var source = Environment.GetEnvironmentVariable("SUPPORT_CASE_ARTIFACT_VERIFY_SOURCE");
        var output = Environment.GetEnvironmentVariable("SUPPORT_CASE_ARTIFACT_VERIFY_OUTPUT");
        var expectedSourceHash = Environment.GetEnvironmentVariable("SUPPORT_CASE_ARTIFACT_VERIFY_SOURCE_SHA256");
        if (string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(output)
            || string.IsNullOrWhiteSpace(expectedSourceHash))
        {
            return;
        }

        Assert.True(File.Exists(source));
        Assert.True(File.Exists(output));
        Assert.Equal(expectedSourceHash, ComputeHash(source), ignoreCase: true);

        var sourceStructure = ReadWorkbookStructure(source);
        var outputStructure = ReadWorkbookStructure(output);
        Assert.Equal(sourceStructure.SheetNames.Length, outputStructure.SheetNames.Length);
        Assert.Equal(sourceStructure.Formulas, outputStructure.Formulas);
        Assert.Equal(sourceStructure.NonTextValues, outputStructure.NonTextValues);
        Assert.Equal(sourceStructure.StyleIndexes, outputStructure.StyleIndexes);
        Assert.Equal(sourceStructure.MergedRanges, outputStructure.MergedRanges);
        Assert.Equal(sourceStructure.CommentReferences, outputStructure.CommentReferences);

        var extractor = new ExcelTextExtractor();
        var sourceText = await extractor.ExtractAsync(source);
        var outputText = await extractor.ExtractAsync(output);
        var targets = sourceText.Entries.Where(static item => item.ShouldTranslate).ToArray();

        Assert.NotEmpty(targets);
        Assert.DoesNotContain(outputText.Entries, static item => item.ShouldTranslate);
    }

    private static ArtifactCreationRequest CreateRequest(string caseFolder, string source, string destination)
    {
        return new ArtifactCreationRequest
        {
            CaseFolder = caseFolder,
            SourceFilePath = source,
            DestinationFolder = destination,
            OutputFileName = "Inquiry_Details_EN.xlsx",
            ProductName = "Checkmarx",
            UserInstruction = "英訳して別名保存",
        };
    }

    private static IReadOnlyList<ExcelTranslationValue> TranslateAll(ArtifactCreationPlan plan)
    {
        return plan.Excel.Entries
            .Where(static item => item.ShouldTranslate)
            .Select(item => new ExcelTranslationValue
            {
                Sheet = item.Sheet,
                Cell = item.Cell,
                SourceText = item.SourceText,
                TranslatedText = item.TargetKind switch
                {
                    ExcelTranslationTargetKind.SheetName when item.SourceText == "問い合わせ" => "Inquiry",
                    ExcelTranslationTargetKind.SheetName when item.SourceText == "追加情報" => "Additional Information",
                    ExcelTranslationTargetKind.DrawingText => "Japanese text inside a drawing",
                    _ => (item.Sheet, item.Cell) switch
                    {
                        ("問い合わせ", "A1") => "Assessment of Secure Coding Check Results",
                        ("問い合わせ", "A3") => "Merged cell text",
                        ("追加情報", "A1") => "Additional information",
                        _ => item.SourceText,
                    },
                },
            })
            .ToArray();
    }

    private static string CreateWorkbook(string directory)
    {
        var path = Path.Combine(directory, "問い合わせ内容.xlsx");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateStylesheet();
        stylesPart.Stylesheet.Save();
        var sharedPart = workbookPart.AddNewPart<SharedStringTablePart>();
        sharedPart.SharedStringTable = new SharedStringTable(
            new SharedStringItem(new Text("セキュアコーディングチェック結果の評価")));
        sharedPart.SharedStringTable.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());

        var firstPart = workbookPart.AddNewPart<WorksheetPart>();
        var firstData = new SheetData(
            new Row(
                new Cell { CellReference = "A1", DataType = CellValues.SharedString, CellValue = new CellValue("0"), StyleIndex = 1U },
                InlineCell("B1", "Checkmarx"),
                new Cell { CellReference = "C1", CellFormula = new CellFormula("SUM(D1:E1)"), CellValue = new CellValue("3") },
                NumberCell("D1", "1"),
                NumberCell("E1", "2"),
                new Cell { CellReference = "F1", CellValue = new CellValue("45292"), StyleIndex = 2U }),
            new Row(
                new Cell
                {
                    CellReference = "C2",
                    DataType = CellValues.String,
                    CellFormula = new CellFormula("\"計算結果\""),
                    CellValue = new CellValue("計算結果"),
                }),
            new Row(InlineCell("A3", "結合セルの日本語")));
        firstPart.Worksheet = new Worksheet(
            firstData,
            new MergeCells(new MergeCell { Reference = "A3:B3" }));
        firstPart.Worksheet.Save();
        AddComment(firstPart, "A1");
        AddDrawingText(firstPart, "図形内の日本語");
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(firstPart),
            SheetId = 1U,
            Name = "問い合わせ",
        });

        var secondPart = workbookPart.AddNewPart<WorksheetPart>();
        secondPart.Worksheet = new Worksheet(new SheetData(new Row(InlineCell("A1", "追加情報です"))));
        secondPart.Worksheet.Save();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(secondPart),
            SheetId = 2U,
            Name = "追加情報",
        });
        workbookPart.Workbook.Save();
        return path;
    }

    private static Stylesheet CreateStylesheet()
    {
        return new Stylesheet(
            new Fonts(new Font()),
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(
                    new ForegroundColor { Rgb = "FFFFFF00" },
                    new BackgroundColor { Indexed = 64U })
                { PatternType = PatternValues.Solid })),
            new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(
                new CellFormat(),
                new CellFormat { FillId = 2U, ApplyFill = true },
                new CellFormat { NumberFormatId = 14U, ApplyNumberFormat = true }));
    }

    private static Cell InlineCell(string reference, string text)
    {
        return new Cell
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(text)),
        };
    }

    private static Cell NumberCell(string reference, string value)
    {
        return new Cell { CellReference = reference, CellValue = new CellValue(value) };
    }

    private static void AddComment(WorksheetPart worksheetPart, string reference)
    {
        var commentsPart = worksheetPart.AddNewPart<WorksheetCommentsPart>();
        commentsPart.Comments = new Comments(
            new Authors(new Author("Tester")),
            new CommentList(
                new Comment(
                    new CommentText(new Run(new Text("確認用コメント"))))
                {
                    Reference = reference,
                    AuthorId = 0U,
                }));
        commentsPart.Comments.Save();
    }

    private static void AddDrawingText(WorksheetPart worksheetPart, string text)
    {
        var drawingsPart = worksheetPart.AddNewPart<DrawingsPart>();
        var shape = new Xdr.Shape(
            new Xdr.NonVisualShapeProperties(
                new Xdr.NonVisualDrawingProperties { Id = 2U, Name = "Translation shape" },
                new Xdr.NonVisualShapeDrawingProperties()),
            new Xdr.ShapeProperties(
                new A.PresetGeometry(new A.AdjustValueList())
                {
                    Preset = A.ShapeTypeValues.RoundRectangle,
                }),
            new Xdr.TextBody(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(new A.Run(new A.Text(text)))));
        var anchor = new Xdr.TwoCellAnchor(
            new Xdr.FromMarker(
                new Xdr.ColumnId("0"),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId("4"),
                new Xdr.RowOffset("0")),
            new Xdr.ToMarker(
                new Xdr.ColumnId("4"),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId("8"),
                new Xdr.RowOffset("0")),
            shape,
            new Xdr.ClientData());
        drawingsPart.WorksheetDrawing = new Xdr.WorksheetDrawing(anchor);
        drawingsPart.WorksheetDrawing.Save();
        worksheetPart.Worksheet!.Append(
            new DocumentFormat.OpenXml.Spreadsheet.Drawing
            {
                Id = worksheetPart.GetIdOfPart(drawingsPart),
            });
        worksheetPart.Worksheet.Save();
    }

    private static string ReadDrawingText(string path, string sheetName)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var worksheetPart = FindWorksheet(document.WorkbookPart!, sheetName);
        return string.Concat(
            worksheetPart.DrawingsPart!.WorksheetDrawing!
                .Descendants<A.Text>()
                .Select(static item => item.Text));
    }

    private static string ReadText(string path, string sheetName, string reference)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart!;
        var cell = FindCell(workbookPart, sheetName, reference);
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            var index = int.Parse(cell.CellValue!.Text);
            return workbookPart.SharedStringTablePart!.SharedStringTable!.ChildElements[index].InnerText;
        }

        return cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? string.Empty;
    }

    private static string ReadFormula(string path, string sheetName, string reference)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return FindCell(document.WorkbookPart!, sheetName, reference).CellFormula?.Text ?? string.Empty;
    }

    private static uint ReadStyleIndex(string path, string sheetName, string reference)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return FindCell(document.WorkbookPart!, sheetName, reference).StyleIndex?.Value ?? 0U;
    }

    private static string ReadCellValue(string path, string sheetName, string reference)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return FindCell(document.WorkbookPart!, sheetName, reference).CellValue?.Text ?? string.Empty;
    }

    private static string ReadMergedRange(string path, string sheetName)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var worksheet = FindWorksheet(document.WorkbookPart!, sheetName);
        return worksheet.Worksheet!.Descendants<MergeCell>().Single().Reference?.Value ?? string.Empty;
    }

    private static Cell FindCell(WorkbookPart workbookPart, string sheetName, string reference)
    {
        return FindWorksheet(workbookPart, sheetName).Worksheet!.Descendants<Cell>()
            .Single(cell => cell.CellReference?.Value == reference);
    }

    private static WorksheetPart FindWorksheet(WorkbookPart workbookPart, string sheetName)
    {
        var sheet = workbookPart.Workbook!.Sheets!.Elements<Sheet>()
            .Single(item => item.Name?.Value == sheetName);
        return (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
    }

    private static string ComputeHash(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static WorkbookStructure ReadWorkbookStructure(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart!;
        var sheets = workbookPart.Workbook!.Sheets!.Elements<Sheet>().ToArray();
        var formulas = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var nonTextValues = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var styleIndexes = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        var mergedRanges = new SortedSet<string>(StringComparer.Ordinal);
        var commentReferences = new SortedSet<string>(StringComparer.Ordinal);

        for (var sheetIndex = 0; sheetIndex < sheets.Length; sheetIndex++)
        {
            var sheet = sheets[sheetIndex];
            var sheetKey = sheetIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
            foreach (var cell in worksheetPart.Worksheet!.Descendants<Cell>())
            {
                var reference = cell.CellReference?.Value ?? string.Empty;
                var key = $"{sheetKey}\u001f{reference}";
                styleIndexes[key] = cell.StyleIndex?.Value ?? 0U;
                if (cell.CellFormula is not null)
                {
                    formulas[key] = $"{cell.CellFormula.OuterXml}\u001f{cell.CellValue?.Text}";
                }
                else
                {
                    var dataType = cell.DataType?.Value;
                    var isText = dataType is not null
                        && (dataType == CellValues.SharedString
                            || dataType == CellValues.InlineString
                            || dataType == CellValues.String);
                    if (!isText)
                    {
                        nonTextValues[key] = $"{dataType}\u001f{cell.CellValue?.Text}";
                    }
                }
            }

            foreach (var merge in worksheetPart.Worksheet.Descendants<MergeCell>())
            {
                mergedRanges.Add($"{sheetKey}\u001f{merge.Reference?.Value}");
            }

            foreach (var comment in worksheetPart.WorksheetCommentsPart?.Comments?.CommentList?.Elements<Comment>() ?? [])
            {
                commentReferences.Add($"{sheetKey}\u001f{comment.Reference?.Value}");
            }
        }

        return new WorkbookStructure(
            sheets.Select(static sheet => sheet.Name!.Value!).ToArray(),
            formulas.ToArray(),
            nonTextValues.ToArray(),
            styleIndexes.ToArray(),
            mergedRanges.ToArray(),
            commentReferences.ToArray());
    }

    private sealed record WorkbookStructure(
        string[] SheetNames,
        KeyValuePair<string, string>[] Formulas,
        KeyValuePair<string, string>[] NonTextValues,
        KeyValuePair<string, uint>[] StyleIndexes,
        string[] MergedRanges,
        string[] CommentReferences);
}
