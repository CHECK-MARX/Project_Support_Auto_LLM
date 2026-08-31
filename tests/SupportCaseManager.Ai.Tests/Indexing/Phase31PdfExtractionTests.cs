using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Indexing;

public sealed class Phase31PdfExtractionTests
{
    [Fact]
    public async Task PdfExtraction_PreservesCliWordBoundary()
    {
        var manual = await ExtractPdfAsync(
            new("qacli", 72, 720),
            new("analyze", 110, 720));

        Assert.Contains("qacli analyze", manual.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("qaclianalyze", manual.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfExtraction_PreservesOptionBoundaries()
    {
        var manual = await ExtractPdfAsync(
            new("qacli", 72, 720),
            new("analyze", 110, 720),
            new("-cf", 165, 720),
            new("-P", 190, 720),
            new("<directory>", 210, 720));

        Assert.Contains("qacli analyze -cf -P <directory>", manual.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfExtraction_DoesNotSplitCompactOptionToken()
    {
        var manual = await ExtractPdfAsync(
            new("qacli", 72, 720),
            new("analyze", 110, 720),
            new("-P<directory>", 165, 720));

        Assert.Contains("-P<directory>", manual.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("-P <directory>", manual.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfExtraction_SeparatesCommandFromFootnote()
    {
        var manual = await ExtractPdfAsync(
            new("qacli", 72, 720),
            new("analyze", 110, 720),
            new("-P", 165, 720),
            new("<directory>", 185, 720),
            new("1", 72, 680),
            new("\"R\" is Relational.", 82, 680));

        Assert.Contains($"<directory>{Environment.NewLine}1", manual.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<directory>1", manual.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfExtraction_SeparatesCommandFromFollowingHeading()
    {
        var manual = await ExtractPdfAsync(
            new("qacli", 72, 720),
            new("analyze", 110, 720),
            new("-F", 165, 720),
            new("<file-with-list>", 185, 720),
            new("Project configuration", 72, 670));

        Assert.Contains($"<file-with-list>{Environment.NewLine}Project configuration", manual.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfExtraction_SeparatesDistantColumnsOnTheSameBaseline()
    {
        var manual = await ExtractPdfAsync(
            new("qacli analyze", 72, 720),
            new("unrelated table cell", 360, 720));

        Assert.Contains($"qacli analyze{Environment.NewLine}unrelated table cell", manual.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonPdfExtraction_PreservesNormalJapaneseText()
    {
        using var temp = new TempDirectory();
        var manuals = Path.Combine(temp.Path, "manuals");
        var index = Path.Combine(temp.Path, "index");
        Directory.CreateDirectory(manuals);
        const string expected = "解析設定を確認してからプロジェクトを実行します。";
        await File.WriteAllTextAsync(Path.Combine(manuals, "guide.txt"), expected, Encoding.UTF8);

        var result = await CreateBuilder().BuildAsync(manuals, index);
        var document = await ReadIndexAsync(result.IndexFilePath);

        Assert.Equal(expected, Assert.Single(document.Manuals).Text);
    }

    [Fact]
    public async Task PdfExtraction_PreservesNormalEnglishSentence()
    {
        var manual = await ExtractPdfAsync(
            new("Run", 72, 720),
            new("the", 100, 720),
            new("analysis", 122, 720),
            new("after", 170, 720),
            new("configuration.", 200, 720));

        Assert.Contains("Run the analysis after configuration.", manual.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Phase30Integrity_StillRejectsAnAmbiguousSourceCommand()
    {
        var manual = await ExtractPdfAsync(
            new("qacli", 72, 720),
            new("analyze", 110, 720),
            new("-cf", 165, 720),
            new("-P<directory>-F<file-with-list>PerforceQAC", 190, 720));

        var record = Assert.Single(HowToAnswerComposer.ExtractAnalysisCommandProvenance(Source(manual.Text)));

        Assert.Equal(HowToAnswerComposer.CliCommandIntegrity.Ambiguous, record.Integrity);
    }

    [Fact]
    public async Task Phase30Integrity_AcceptsACompleteExtractedCommand()
    {
        var manual = await ExtractPdfAsync(
            new("qacli", 72, 720),
            new("analyze", 110, 720),
            new("-cf", 165, 720),
            new("-P", 190, 720),
            new("<directory>", 210, 720));

        var record = Assert.Single(HowToAnswerComposer.ExtractAnalysisCommandProvenance(Source(manual.Text)));

        Assert.Equal(HowToAnswerComposer.CliCommandIntegrity.Complete, record.Integrity);
        Assert.Equal("qacli analyze -cf -P <directory>", record.NormalizedCommandText);
    }

    [Fact]
    public async Task ActualQacManual_RebuildsAndValidatesOnlyInPhase31Staging_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SCM_RUN_PHASE31_STAGING"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsPath = Environment.GetEnvironmentVariable("SCM_LIVE_SETTINGS_PATH") ??
            Path.Combine(localAppData, "SupportCaseManager", "ai-data", "settings.json");
        var settings = JsonSerializer.Deserialize<AiAssistantSettings>(
            await File.ReadAllTextAsync(settingsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidOperationException("AI settings could not be loaded.");
        var product = settings.Products.Single(item =>
            item.ProductName.Equals("HelixQAC", StringComparison.OrdinalIgnoreCase));
        var activeRoot = string.IsNullOrWhiteSpace(settings.AiIndexFolder)
            ? Path.Combine(localAppData, "SupportCaseManager", "ai-index")
            : settings.AiIndexFolder;
        var activeIndexPath = Path.Combine(
            ProductIndexPathResolver.GetProductIndexFolder(activeRoot, product.ProductName),
            AiManualIndexBuilder.IndexFileName);
        var activeHashBefore = await Sha256Async(activeIndexPath);
        var activeIndex = await ReadIndexAsync(activeIndexPath);
        var activeTargetChunks = activeIndex.Manuals
            .Where(item => IsTargetQacManual(item.DocumentTitle) && item.PageNumber is 40 or 41)
            .ToList();
        var stagingFolder = Environment.GetEnvironmentVariable("SCM_PHASE31_STAGING_INDEX") ??
            Path.Combine(Path.GetTempPath(), "SupportCaseManager", "phase31", "staging", product.ProductName);
        Directory.CreateDirectory(stagingFolder);

        var build = await new AiManualIndexBuilder().BuildManyAsync(product.ManualFolders, stagingFolder);
        var stagedIndex = await ReadIndexAsync(build.IndexFilePath);
        var targetChunks = stagedIndex.Manuals
            .Where(item => IsTargetQacManual(item.DocumentTitle) && item.PageNumber is 40 or 41)
            .ToList();
        var relevant = targetChunks
            .Where(item => item.Text.Contains("qacli analyze", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var searchResults = await new AiManualKeywordSearcher().SearchAsync(
            stagingFolder,
            "QAC 解析 CLI qacli analyze コマンド オプション",
            36);
        var provenance = searchResults
            .SelectMany(HowToAnswerComposer.ExtractAnalysisCommandProvenance)
            .ToList();
        var composed = HowToAnswerComposer.TryComposeAnalysisCli(new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = product.ProductName },
            InquiryText = "QACの解析CLIコマンドとオプションを教えてください。",
            Sources = searchResults,
            Settings = new AiAssistantSettings { MaxEvidenceItems = 5 },
        }, out var deterministicAnswer);
        var activeHashAfter = await Sha256Async(activeIndexPath);

        var reportPath = Environment.GetEnvironmentVariable("SCM_PHASE31_REPORT") ??
            Path.Combine(Path.GetTempPath(), "SupportCaseManager", "phase31", "phase31-staging-report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            DataClassification = "local-aggregate-no-document-content",
            Product = product.ProductName,
            StagingIndex = build.IndexFilePath,
            build.IndexedFileCount,
            build.IndexedChunkCount,
            build.ZipDerivedChunkCount,
            build.ErrorCount,
            ActiveIndexUnchanged = activeHashBefore == activeHashAfter,
            ActiveIndexBytes = new FileInfo(activeIndexPath).Length,
            StagingIndexBytes = new FileInfo(build.IndexFilePath).Length,
            Before = new
            {
                TargetChunks = activeTargetChunks.Count,
                CollapsedQacliAnalyzeChunks = activeTargetChunks.Count(item =>
                    item.Text.Contains("qaclianalyze", StringComparison.OrdinalIgnoreCase)),
                FusedFootnoteChunks = activeTargetChunks.Count(item =>
                    item.Text.Contains("<directory>1\"R\"", StringComparison.OrdinalIgnoreCase)),
                FusedHeadingChunks = activeTargetChunks.Count(item =>
                    item.Text.Contains("<file-with-list>Perforce", StringComparison.OrdinalIgnoreCase)),
            },
            After = new
            {
                TargetChunks = targetChunks.Count,
                CollapsedQacliAnalyzeChunks = targetChunks.Count(item =>
                    item.Text.Contains("qaclianalyze", StringComparison.OrdinalIgnoreCase)),
                FusedFootnoteChunks = targetChunks.Count(item =>
                    item.Text.Contains("<directory>1\"R\"", StringComparison.OrdinalIgnoreCase)),
                FusedHeadingChunks = targetChunks.Count(item =>
                    item.Text.Contains("<file-with-list>Perforce", StringComparison.OrdinalIgnoreCase)),
            },
            RelevantCommandChunks = relevant.Count,
            CorpusCollapsedQacliAnalyzeChunks = stagedIndex.Manuals.Count(item =>
                item.Text.Contains("qaclianalyze", StringComparison.OrdinalIgnoreCase)),
            TargetCollapsedQacliAnalyzeChunks = targetChunks.Count(item =>
                item.Text.Contains("qaclianalyze", StringComparison.OrdinalIgnoreCase)),
            FusedFootnoteChunks = targetChunks.Count(item =>
                item.Text.Contains("<directory>1\"R\"", StringComparison.OrdinalIgnoreCase) ||
                item.Text.Contains("<directory>1 \"R\"", StringComparison.OrdinalIgnoreCase)),
            FusedHeadingChunks = targetChunks.Count(item =>
                item.Text.Contains("<file-with-list>Perforce", StringComparison.OrdinalIgnoreCase)),
            SearchResultCount = searchResults.Count,
            CompleteCommandCount = provenance.Count(item =>
                item.Integrity == HowToAnswerComposer.CliCommandIntegrity.Complete),
            AmbiguousCommandCount = provenance.Count(item =>
                item.Integrity == HowToAnswerComposer.CliCommandIntegrity.Ambiguous),
            RejectedCommandCount = provenance.Count(item =>
                item.Integrity is HowToAnswerComposer.CliCommandIntegrity.Incomplete or
                    HowToAnswerComposer.CliCommandIntegrity.Rejected),
            DeterministicAnswerComposed = composed,
            DeterministicAnswerContainsAnalyzeCommand = deterministicAnswer.Contains(
                "qacli analyze", StringComparison.OrdinalIgnoreCase),
        }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(0, build.ErrorCount);
        Assert.NotEmpty(relevant);
        Assert.DoesNotContain(targetChunks, item =>
            item.Text.Contains("qaclianalyze", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(relevant, item =>
            item.Text.Contains("qacli analyze -cf -P <directory>", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(relevant, item =>
            item.Text.Contains("-F <file-with-list>", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(targetChunks, item =>
            item.Text.Contains("<directory>1\"R\"", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(targetChunks, item =>
            item.Text.Contains("<file-with-list>Perforce", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(provenance, item =>
            item.Integrity == HowToAnswerComposer.CliCommandIntegrity.Complete);
        Assert.True(composed);
        Assert.Contains("qacli analyze", deterministicAnswer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(activeHashBefore, activeHashAfter);
    }

    private static AiManualIndexBuilder CreateBuilder() =>
        new(() => new DateTimeOffset(2026, 8, 31, 18, 0, 0, TimeSpan.FromHours(9)));

    private static SearchSource Source(string text) => new()
    {
        SourceId = "phase31-pdf",
        SourceType = "Manual",
        Title = "QAC Manual",
        DocumentTitle = "QAC Manual",
        Text = text,
        Score = 1.0,
    };

    private static async Task<AiIndexedManual> ExtractPdfAsync(params PdfTextFragment[] fragments)
    {
        using var temp = new TempDirectory();
        var manuals = Path.Combine(temp.Path, "manuals");
        var index = Path.Combine(temp.Path, "index");
        Directory.CreateDirectory(manuals);
        await WritePositionedPdfAsync(Path.Combine(manuals, "manual.pdf"), fragments);

        var result = await CreateBuilder().BuildAsync(manuals, index);
        var document = await ReadIndexAsync(result.IndexFilePath);
        return Assert.Single(document.Manuals);
    }

    private static async Task<AiManualIndexDocument> ReadIndexAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AiManualIndexDocument>(stream)
            ?? throw new InvalidOperationException("Manual index JSON could not be deserialized.");
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static bool IsTargetQacManual(string? title) =>
        title?.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Equals("PerforceQACManual", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task WritePositionedPdfAsync(string path, IReadOnlyList<PdfTextFragment> fragments)
    {
        var content = string.Join(
            "\n",
            fragments.Select(fragment =>
                $"BT /F1 12 Tf {fragment.X} {fragment.Y} Td ({EscapePdfText(fragment.Text)}) Tj ET"));
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(offset.ToString("0000000000"));
            builder.Append(" 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.ASCII);
    }

    private static string EscapePdfText(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal);

    private sealed record PdfTextFragment(string Text, int X, int Y);
}
