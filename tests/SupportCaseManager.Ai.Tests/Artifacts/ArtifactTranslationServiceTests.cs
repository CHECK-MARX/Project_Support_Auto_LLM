using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SupportCaseManager.Ai.Core.Artifacts;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class ArtifactTranslationServiceTests
{
    [Fact]
    public void PathPolicy_UsesSourceBasenameAndPreservesExtension()
    {
        Assert.Equal("Inquiry_Details_EN.xlsx", CaseArtifactPathPolicy.SuggestDefaultFileName("Inquiry_Details.xlsx"));
        Assert.Equal("Additional_Inquiry_Details_EN.xlsx", CaseArtifactPathPolicy.SuggestDefaultFileName("追加問い合わせ内容.xlsx"));
        Assert.Equal("Inquiry_Details_EN.xlsx", CaseArtifactPathPolicy.SuggestDefaultFileName("問い合わせ内容.xlsx"));
        Assert.Equal("Scan_Results_EN.csv", CaseArtifactPathPolicy.SuggestDefaultFileName("スキャン結果.csv"));
        Assert.Equal("Investigation_Notes_EN.txt", CaseArtifactPathPolicy.SuggestDefaultFileName("調査メモ.txt"));
        Assert.Equal("notes_EN.md", CaseArtifactPathPolicy.SuggestDefaultFileName("notes.md"));
        Assert.Equal("data_EN.csv", CaseArtifactPathPolicy.SuggestDefaultFileName("data.csv"));
        Assert.Equal(
            "CxOne_Default_Config_Project_Settings_Guide_EN.docx",
            CaseArtifactPathPolicy.SuggestDefaultFileName("CxOne_Default_Config_プロジェクト設定手順.docx"));
        Assert.Equal(ArtifactFormat.PlainText, CaseArtifactPathPolicy.GetArtifactFormat("notes.txt"));
        Assert.Equal(ArtifactFormat.WordDocument, CaseArtifactPathPolicy.GetArtifactFormat("guide.docx"));
        Assert.Equal(ArtifactFormat.Unsupported, CaseArtifactPathPolicy.GetArtifactFormat("report.pdf"));
    }

    [Fact]
    public void FilenamePreview_NormalizesNamesAndDoesNotDuplicateEnglishMarker()
    {
        var service = new ArtifactFilenameTranslationService();

        var normalized = service.CreatePreview("調査 メモ: draft?.txt");
        var existingTranslation = service.CreatePreview("Inquiry_Details_EN.txt");
        var datedTranslation = service.CreatePreview("Inquiry_Details_EN_20260908_2.xlsx");

        Assert.Equal("Investigation_Notes_draft_EN.txt", normalized.OutputFileName);
        Assert.False(normalized.UsedFallback);
        Assert.Equal("Inquiry_Details_EN.txt", existingTranslation.OutputFileName);
        Assert.Equal("Inquiry_Details_EN_20260908_2.xlsx", datedTranslation.OutputFileName);
        Assert.DoesNotContain(':', normalized.OutputFileName);
        Assert.DoesNotContain('?', normalized.OutputFileName);
    }

    [Fact]
    public async Task FilenamePreview_DoesNotWriteOrModifySource()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "問い合わせ内容.xlsx");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var before = ComputeHash(source);

        var preview = new ArtifactFilenameTranslationService().CreatePreview(source);

        Assert.Equal("Inquiry_Details_EN.xlsx", preview.OutputFileName);
        Assert.Equal(before, ComputeHash(source));
        Assert.Equal([source], Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void PathPolicy_SuggestsDateNameThenNumberWithoutOverwrite()
    {
        using var temp = new TempDirectory();
        var policy = new CaseArtifactPathPolicy();
        var source = Path.Combine(temp.Path, "問い合わせ内容.txt");
        File.WriteAllText(source, "原文", Encoding.UTF8);

        var dateName = policy.SuggestDateFileName(temp.Path, source, new DateTime(2026, 9, 8));
        Assert.Equal("Inquiry_Details_EN_20260908.txt", dateName);

        File.WriteAllText(Path.Combine(temp.Path, dateName), "existing");
        Assert.Equal(
            "Inquiry_Details_EN_20260908_2.txt",
            policy.SuggestDateFileName(temp.Path, source, new DateTime(2026, 9, 8)));
    }

    [Fact]
    public void RequestDetector_RecognizesExplicitSupportedSourceFormats()
    {
        var detector = new ArtifactRequestDetector();

        Assert.True(detector.IsExplicitArtifactTranslationRequest("問い合わせ.csvを英訳して別名で保存してください"));
        Assert.True(detector.IsExplicitArtifactTranslationRequest("手順.docxを英訳して別名で保存してください"));
        Assert.True(detector.IsExplicitArtifactTranslationRequest("notes.mdを英語に翻訳してコピーを作成"));
        Assert.False(detector.IsExplicitArtifactTranslationRequest("notes.mdを確認してください"));
        Assert.Equal("問い合わせ.csv", detector.FindMentionedSourceFileName("添付の問い合わせ.csvを英訳して保存"));
    }

    [Fact]
    public async Task TextService_CreatesSameFormatCopyAndPreservesSourceEncodingAndHash()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "問い合わせ内容.txt");
        var destination = Path.Combine(temp.Path, "メーカー連携内容");
        var sourceContent = "日本語の概要\r\nhttps://example.test/help\r\n";
        await File.WriteAllTextAsync(source, sourceContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var sourceHash = ComputeHash(source);
        var service = new ArtifactTranslationService();
        var plan = await service.CreatePlanAsync(CreateRequest(temp.Path, source, destination, "問い合わせ内容_EN.txt"));

        Assert.Equal(ArtifactFormat.PlainText, plan.Format);
        Assert.Equal(1, plan.Text.TranslatableCount);
        var entry = Assert.Single(plan.Text.Entries, item => item.ShouldTranslate);
        var result = await service.CreateArtifactAsync(plan,
        [
            new ArtifactTextTranslationValue
            {
                Key = entry.Key,
                SourceText = entry.SourceText,
                TranslatedText = "English summary",
            },
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal(sourceHash, ComputeHash(source));
        Assert.Equal("問い合わせ内容_EN.txt", Path.GetFileName(result.OutputFilePath));
        Assert.Equal("English summary\r\nhttps://example.test/help\r\n", await File.ReadAllTextAsync(result.OutputFilePath));
        Assert.Equal([0xEF, 0xBB, 0xBF], (await File.ReadAllBytesAsync(result.OutputFilePath))[..3]);
    }

    [Fact]
    public async Task CsvService_PreservesDelimiterQuotesAndUntranslatedFields()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "問い合わせ.csv");
        var destination = Path.Combine(temp.Path, "メーカー連携内容");
        await File.WriteAllTextAsync(source, "Name,URL\r\n日本語,\"説明,詳細\"\r\n", Encoding.UTF8);
        var service = new ArtifactTranslationService();
        var plan = await service.CreatePlanAsync(CreateRequest(temp.Path, source, destination, "問い合わせ_EN.csv"));
        var entries = plan.Text.Entries.Where(static item => item.ShouldTranslate).ToArray();

        Assert.Equal(ArtifactFormat.Csv, plan.Format);
        Assert.Equal(2, entries.Length);
        var result = await service.CreateArtifactAsync(
            plan,
            entries.Select(item => new ArtifactTextTranslationValue
            {
                Key = item.Key,
                SourceText = item.SourceText,
                TranslatedText = item.Key.EndsWith(":0", StringComparison.Ordinal)
                    ? "English"
                    : "English, detailed",
            }).ToArray());

        Assert.Equal(
            "Name,URL\r\nEnglish,\"English, detailed\"\r\n",
            await File.ReadAllTextAsync(result.OutputFilePath));
    }

    [Fact]
    public async Task MarkdownService_DoesNotTranslateCodeFenceContents()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "notes.md");
        await File.WriteAllTextAsync(source, "# 日本語見出し\n```text\n日本語コマンド\n```\n");
        var plan = await new ArtifactTranslationService().CreatePlanAsync(
            CreateRequest(temp.Path, source, Path.Combine(temp.Path, "メーカー連携内容"), "notes_EN.md"));

        Assert.Contains(plan.Text.Entries, item => item.ShouldTranslate && item.SourceText == "# 日本語見出し");
        Assert.All(
            plan.Text.Entries.Where(item => item.SourceText.Contains("コマンド", StringComparison.Ordinal)),
            item => Assert.False(item.ShouldTranslate));
    }

    [Fact]
    public async Task WordDocumentService_TranslatesLogicalParagraphsAndPreservesPackageStructure()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "CxOne_Default_Config_プロジェクト設定手順.docx");
        var destination = Path.Combine(temp.Path, "メーカー連携内容");
        CreateWordDocument(source);
        var sourceHash = ComputeHash(source);

        var service = new ArtifactTranslationService();
        var plan = await service.CreatePlanAsync(
            CreateRequest(
                temp.Path,
                source,
                destination,
                "CxOne_Default_Config_Project_Settings_Guide_EN.docx"));

        Assert.Equal(ArtifactFormat.WordDocument, plan.Format);
        Assert.Equal(ArtifactKind.WordEnglishTranslation, plan.Kind);
        Assert.Contains(plan.Text.Entries, item => item.ShouldTranslate && item.Location.StartsWith("本文", StringComparison.Ordinal));
        Assert.Contains(plan.Text.Entries, item => item.ShouldTranslate && item.Location.StartsWith("ヘッダー", StringComparison.Ordinal));
        Assert.Contains(plan.Text.Entries, item => item.ShouldTranslate && item.Location.StartsWith("フッター", StringComparison.Ordinal));
        Assert.Contains(plan.Text.Entries, item => item.ShouldTranslate && item.SourceText.Contains("プロジェクト設定手順", StringComparison.Ordinal));
        Assert.Contains(plan.Text.Entries, item => item.ShouldTranslate && item.SourceText.Contains("現在確認されている", StringComparison.Ordinal));
        Assert.Contains(plan.Text.Entries, item => !item.ShouldTranslate && item.SourceText == "https://example.test/help");
        Assert.Contains(plan.Text.Entries, item => !item.ShouldTranslate && item.SourceText == @"C:\tools\qac");
        Assert.Contains(plan.Text.Entries, item => !item.ShouldTranslate && item.SkipReason == "コードスタイルの段落");
        Assert.Contains(plan.Text.Entries, item => !item.ShouldTranslate && item.SkipReason == "フィールドコードを含む段落");

        var translations = plan.Text.Entries
            .Where(static item => item.ShouldTranslate)
            .Select(item => new ArtifactTextTranslationValue
            {
                Key = item.Key,
                SourceText = item.SourceText,
                TranslatedText = TranslateForTest(item.SourceText),
            })
            .ToArray();
        var result = await service.CreateArtifactAsync(plan, translations);

        Assert.True(result.Succeeded);
        Assert.Equal(sourceHash, ComputeHash(source));
        Assert.False(File.Exists(Path.Combine(destination, Path.GetFileName(source))));
        Assert.True(File.Exists(result.OutputFilePath));
        using var output = WordprocessingDocument.Open(result.OutputFilePath, false);
        var mainPart = Assert.IsType<MainDocumentPart>(output.MainDocumentPart);
        var headerPart = Assert.Single(mainPart.HeaderParts);
        var footerPart = Assert.Single(mainPart.FooterParts);
        Assert.Single(mainPart.ImageParts);
        var mainDocument = Assert.IsType<Document>(mainPart.Document);
        Assert.Single(mainDocument.Body!.Elements<Table>());
        Assert.Contains("Body description", mainDocument.InnerText, StringComparison.Ordinal);
        Assert.Contains("Table description", mainDocument.InnerText, StringComparison.Ordinal);
        Assert.Contains("Default Config Project Settings Guide", mainDocument.InnerText, StringComparison.Ordinal);
        Assert.Contains("Known limitations", mainDocument.InnerText, StringComparison.Ordinal);
        Assert.DoesNotContain("プロジェクト設定手順", mainDocument.InnerText, StringComparison.Ordinal);
        Assert.DoesNotContain("現在確認されている", mainDocument.InnerText, StringComparison.Ordinal);
        Assert.Contains("https://example.test/help", mainDocument.InnerText, StringComparison.Ordinal);
        Assert.Contains(@"C:\tools\qac", mainDocument.InnerText, StringComparison.Ordinal);
        Assert.Contains("Header", Assert.IsType<Header>(headerPart.Header).InnerText, StringComparison.Ordinal);
        Assert.Contains("Footer", Assert.IsType<Footer>(footerPart.Footer).InnerText, StringComparison.Ordinal);
        Assert.NotNull(mainDocument.Descendants<RunProperties>().FirstOrDefault(properties => properties.Bold is not null));
    }

    [Fact]
    public async Task WordDocumentService_RejectsExistingOutputWithoutChangingSource()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "手順.docx");
        var destination = Path.Combine(temp.Path, "メーカー連携内容");
        CreateWordDocument(source);
        var sourceHash = ComputeHash(source);
        Directory.CreateDirectory(destination);
        var output = Path.Combine(destination, "Guide_EN.docx");
        await File.WriteAllTextAsync(output, "existing");

        var plan = await new ArtifactTranslationService().CreatePlanAsync(
            CreateRequest(temp.Path, source, destination, "Guide_EN.docx"));
        var translations = plan.Text.Entries
            .Where(static item => item.ShouldTranslate)
            .Select(item => new ArtifactTextTranslationValue
            {
                Key = item.Key,
                SourceText = item.SourceText,
                TranslatedText = "English",
            })
            .ToArray();

        await Assert.ThrowsAsync<IOException>(() => new ArtifactTranslationService().CreateArtifactAsync(plan, translations));
        Assert.Equal(sourceHash, ComputeHash(source));
        Assert.Equal("existing", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task TextService_RejectsSourceMutationAfterPlan()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "notes.txt");
        var destination = Path.Combine(temp.Path, "メーカー連携内容");
        await File.WriteAllTextAsync(source, "日本語");
        var service = new ArtifactTranslationService();
        var plan = await service.CreatePlanAsync(CreateRequest(temp.Path, source, destination, "notes_EN.txt"));
        var entry = Assert.Single(plan.Text.Entries, item => item.ShouldTranslate);
        await File.AppendAllTextAsync(source, "変更");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateArtifactAsync(
            plan,
            [new ArtifactTextTranslationValue
            {
                Key = entry.Key,
                SourceText = entry.SourceText,
                TranslatedText = "English",
            }]));

        Assert.False(File.Exists(plan.OutputFullPath));
    }

    [Fact]
    public async Task PathPolicy_RejectsUnsupportedAndMismatchedOutputFormats()
    {
        using var temp = new TempDirectory();
        var unsupported = Path.Combine(temp.Path, "report.pdf");
        await File.WriteAllTextAsync(unsupported, "pdf placeholder");
        var service = new ArtifactTranslationService();

        var unsupportedError = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreatePlanAsync(
            CreateRequest(temp.Path, unsupported, Path.Combine(temp.Path, "メーカー連携内容"), "report_EN.pdf")));
        Assert.Contains("対応していません", unsupportedError.Message, StringComparison.Ordinal);

        var source = Path.Combine(temp.Path, "notes.txt");
        await File.WriteAllTextAsync(source, "日本語");
        var mismatchError = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreatePlanAsync(
            CreateRequest(temp.Path, source, Path.Combine(temp.Path, "メーカー連携内容"), "notes_EN.csv")));
        Assert.Contains("拡張子", mismatchError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TranslationJsonParser_RequiresExactApprovedEntries()
    {
        var expected = new[]
        {
            new ArtifactTextTranslationEntry
            {
                Key = "line:0",
                Location = "行 1",
                SourceText = "日本語",
                ShouldTranslate = true,
            },
        };
        var parser = new ArtifactTextTranslationJsonParser();

        var parsed = parser.Parse(
            "[{\"key\":\"line:0\",\"sourceText\":\"日本語\",\"translatedText\":\"English\"}]",
            expected);
        var missing = parser.Parse("[]", expected);

        Assert.True(parsed.Succeeded);
        Assert.False(missing.Succeeded);
        Assert.Contains(missing.Errors, item => item.Contains("項目がありません", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectedSource_IsLimitedToCaseFolder()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var source = Path.Combine(outside.Path, "notes.txt");
        await File.WriteAllTextAsync(source, "日本語");
        var policy = new CaseArtifactPathPolicy();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() =>
            policy.NormalizeSelectedSourceFile(temp.Path, source)));
    }

    private static ArtifactCreationRequest CreateRequest(
        string caseFolder,
        string source,
        string destination,
        string outputFileName) => new()
        {
            CaseFolder = caseFolder,
            SourceFilePath = source,
            DestinationFolder = destination,
            OutputFileName = outputFileName,
            ProductName = "Synthetic",
            UserInstruction = "英訳して別名で保存",
        };

    private static string ComputeHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void CreateWordDocument(string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var headerPart = mainPart.AddNewPart<HeaderPart>();
        headerPart.Header = new Header(new Paragraph(new Run(new Text("ヘッダー"))));
        var footerPart = mainPart.AddNewPart<FooterPart>();
        footerPart.Footer = new Footer(new Paragraph(new Run(new Text("フッター"))));
        var imagePart = mainPart.AddImagePart(ImagePartType.Png);
        imagePart.FeedData(new MemoryStream(
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
            0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0xF0,
            0x1F, 0x00, 0x05, 0x00, 0x01, 0xFF, 0x89, 0x99,
            0x3D, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
            0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
        ]));
        var table = new Table();
        var row = new TableRow();
        row.Append(new TableCell(new Paragraph(new Run(new Text("表の説明")))));
        table.Append(row);
        mainPart.Document = new Document(
            new Body(
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    new Run(new RunProperties(new Bold()), new Text("本文の説明"))),
                table,
                new Paragraph(
                    new Run(new RunProperties(new Bold()), new Text("Default Config ")),
                    new Run(new RunProperties(new Italic()), new Text("プロジェクト設定手順"))),
                new Paragraph(
                    new Run(new Text("現在確認されている")),
                    new Run(new Break()),
                    new Run(new Text("制限事項"))),
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Code" }),
                    new Run(new Text("コード内の日本語"))),
                new Paragraph(
                    new SimpleField { Instruction = "PAGE" },
                    new Run(new Text("フィールド"))),
                new Paragraph(new Run(new Text("https://example.test/help"))),
                new Paragraph(new Run(new Text(@"C:\tools\qac"))),
                new SectionProperties(
                    new HeaderReference
                    {
                        Type = HeaderFooterValues.Default,
                        Id = mainPart.GetIdOfPart(headerPart),
                    },
                    new FooterReference
                    {
                        Type = HeaderFooterValues.Default,
                        Id = mainPart.GetIdOfPart(footerPart),
                    })));
        mainPart.Document.Save();
        headerPart.Header.Save();
        footerPart.Footer.Save();
    }

    private static string TranslateForTest(string source) => source switch
    {
        "本文の説明" => "Body description",
        "表の説明" => "Table description",
        "ヘッダー" => "Header",
        "フッター" => "Footer",
        var value when value.Contains("プロジェクト設定手順", StringComparison.Ordinal) => "Default Config Project Settings Guide",
        var value when value.Contains("現在確認されている", StringComparison.Ordinal) => "Known limitations",
        _ => "English translation",
    };
}
