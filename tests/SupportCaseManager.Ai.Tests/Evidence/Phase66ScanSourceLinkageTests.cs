using System.IO.Compression;
using System.Text;
using SupportCaseManager.Ai.Core.Evidence;

namespace SupportCaseManager.Ai.Tests.Evidence;

public sealed class Phase66ScanSourceLinkageTests
{
    [Fact]
    public void ParsesAnonymousScanCsvFields()
    {
        const string csv = "FindingId,Query,File,Line,Source,Sink,Result Path,Severity\n" +
                           "f-001,Example Check,src\\app.asp,3,source-role,sink-role,node-1,High\n";

        var finding = Assert.Single(ScanResultParser.ParseCsv(csv));

        Assert.Equal("f-001", finding.FindingId);
        Assert.Equal("src\\app.asp", finding.ReportedPath);
        Assert.Equal("app.asp", finding.ReportedFile);
        Assert.Equal(3, finding.ReportedLine);
        Assert.Equal("source-role", finding.Source);
        Assert.Equal("sink-role", finding.Sink);
        Assert.Equal("node-1", finding.ResultPath);
    }

    [Fact]
    public void ParsesPdfTextFindingBlocksWithoutGuessingLine()
    {
        var findings = ScanResultParser.ParseText("File: src/app.cs\nLine: 2\nFile: ./src/other.cs\nLine: 1");

        Assert.Equal(2, findings.Count);
        Assert.Equal("src/app.cs", findings[0].ReportedPath);
        Assert.Equal(2, findings[0].ReportedLine);
        Assert.Equal("./src/other.cs", findings[1].ReportedPath);
        Assert.Equal(1, findings[1].ReportedLine);
    }

    [Fact]
    public async Task ResolvesDeterministicallyAndRetainsBoundedProvenance()
    {
        using var fixture = new ZipFixture();
        fixture.Add("src/app.asp", "line-1\nline-2\nneedle\nline-4\nline-5");
        fixture.Add("unique/only.py", "print('only')");
        fixture.Add("duplicate/name.asp", "duplicate-a");
        fixture.Add("other/name.asp", "duplicate-b");
        fixture.Add("assets/ignored.png", "binary-placeholder");
        fixture.Add("../outside.asp", "escape");
        fixture.Add("/root.asp", "absolute");
        fixture.Save();

        var findings = new[]
        {
            new ScanResultFinding { FindingId = "exact", ReportedPath = "src/app.asp", ReportedLine = 3, Source = "source", Sink = "sink", ResultPath = "node-1" },
            new ScanResultFinding { FindingId = "suffix", ReportedPath = "C:/build/project/src/app.asp", ReportedLine = 3 },
            new ScanResultFinding { FindingId = "filename", ReportedPath = "./only.py", ReportedLine = 1 },
            new ScanResultFinding { FindingId = "ambiguous", ReportedPath = "name.asp", ReportedLine = 1 },
            new ScanResultFinding { FindingId = "traversal", ReportedPath = "../src/app.asp", ReportedLine = 1 },
            new ScanResultFinding { FindingId = "invalid-line", ReportedPath = "src/app.asp", ReportedLine = 99 },
        };

        var result = await new ScanSourceLinker().LinkAsync(findings, fixture.Path, "case-a");

        Assert.Equal(ScanSourceMatchKind.ExactLogicalPath, result.Decisions[0].MatchKind);
        Assert.Equal(ScanSourceMatchKind.UniqueSuffixPath, result.Decisions[1].MatchKind);
        Assert.Equal(ScanSourceMatchKind.UniqueFilenameOnly, result.Decisions[2].MatchKind);
        Assert.Equal("AMBIGUOUS_SOURCE", result.Decisions[3].Status);
        Assert.Equal(ScanSourceMatchKind.InvalidPath, result.Decisions[4].MatchKind);
        Assert.Equal("INVALID_LINE", result.Decisions[5].Status);
        Assert.Equal(4, result.ZipEntries.Count);
        Assert.NotEmpty(result.Warnings);

        var evidence = Assert.Single(result.SourceContextEvidence, item => item.ScanEvidenceId == "exact");
        Assert.Equal("SourceContext", evidence.EvidenceKind);
        Assert.Equal("src/app.asp", evidence.EntryPath);
        Assert.Equal(3, evidence.ReportedLine);
        Assert.Equal(1, evidence.ContextStartLine);
        Assert.Equal(5, evidence.ContextEndLine);
        Assert.Equal("source", evidence.SourceRole);
        Assert.Equal("sink", evidence.SinkRole);
        Assert.Equal("node-1", evidence.ResultPath);
        Assert.DoesNotContain(Path.GetTempPath(), evidence.Locator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("binary-placeholder", evidence.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNotCreateEvidenceForUnreadableSourceText()
    {
        using var fixture = new ZipFixture();
        fixture.AddBytes("src/broken.cs", [0xFF, 0xFE, 0x00]);
        fixture.Save();

        var result = await new ScanSourceLinker().LinkAsync(
            [new ScanResultFinding { FindingId = "broken", ReportedPath = "src/broken.cs", ReportedLine = 1 }],
            fixture.Path,
            "case-a");

        Assert.Equal("UNREADABLE_TEXT", Assert.Single(result.Decisions).Status);
        Assert.Empty(result.SourceContextEvidence);
    }

    [Fact]
    public async Task IntegratesScanFindingWithCurrentCaseEvidenceWithoutCrossCaseLeakage()
    {
        using var fixture = new ZipFixture();
        fixture.Add("src/app.cs", "using System;\nvar value = 42;\nreturn value;");
        fixture.Save();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "scan.csv"),
            "FindingId,File,Line,Source,Sink\nfind-a,src/app.cs,2,source-a,sink-a\n", Encoding.UTF8);

        var service = new CurrentCaseEvidenceService();
        var first = await service.BuildAsync(fixture.Root, "case-a", "value");
        var second = await service.BuildAsync(fixture.Root, "case-b", "value");

        var source = Assert.Single(first.Evidence, item => item.EvidenceKind == "SourceContext");
        Assert.Equal("find-a", source.ScanEvidenceId);
        Assert.Equal("src/app.cs", source.EntryPath);
        Assert.Contains("2: var value = 42;", source.Text, StringComparison.Ordinal);
        Assert.All(second.Evidence, item => Assert.Equal("case-b", item.CaseSessionId));
        Assert.DoesNotContain(second.Evidence, item => item.CaseSessionId == "case-a");
    }

    private sealed class ZipFixture : IDisposable
    {
        public ZipFixture()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SupportCaseManager", "phase66", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Path = System.IO.Path.Combine(Root, "source.zip");
            Archive = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }

        public string Root { get; }
        public string Path { get; }
        private Dictionary<string, byte[]> Archive { get; }

        public void Add(string entryPath, string content) => Archive[entryPath] = Encoding.UTF8.GetBytes(content);
        public void AddBytes(string entryPath, byte[] content) => Archive[entryPath] = content;

        public void Save()
        {
            using var archive = ZipFile.Open(Path, ZipArchiveMode.Create);
            foreach (var item in Archive)
            {
                using var stream = archive.CreateEntry(item.Key).Open();
                stream.Write(item.Value);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
