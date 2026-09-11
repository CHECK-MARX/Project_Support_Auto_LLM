using System.Security.Cryptography;
using System.Text;
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
        Assert.Equal(ArtifactFormat.PlainText, CaseArtifactPathPolicy.GetArtifactFormat("notes.txt"));
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
}
