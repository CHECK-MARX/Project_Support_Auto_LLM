using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SupportCaseManager.Ai.Core.Artifacts;
using SupportCaseManager.Ai.Tests.Helpers;
using P = DocumentFormat.OpenXml.Presentation;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class PowerPointTranslationTests
{
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";

    [Theory]
    [InlineData("proposal.pptx")]
    [InlineData("proposal.PPTX")]
    public void PowerPoint_IsRecognizedByEveryEntryPoint(string fileName)
    {
        Assert.Equal(ArtifactFormat.PowerPointPresentation, CaseArtifactPathPolicy.GetArtifactFormat(fileName));
        Assert.True(CaseArtifactPathPolicy.IsSupportedSource(fileName));
        CaseArtifactPathPolicy.ValidateOutputFileName(fileName, ArtifactFormat.PowerPointPresentation);
        var detector = new ArtifactRequestDetector();
        Assert.True(detector.IsExplicitArtifactTranslationRequest($"添付の{fileName}を英訳して別名で保存"));
        Assert.Equal(fileName, detector.FindMentionedSourceFileName($"添付の{fileName}を英訳して別名で保存"));
        Assert.True(detector.IsExplicitArtifactTranslationRequest("PowerPointを英訳してコピーを作成"));
    }

    [Fact]
    public async Task Translation_PreservesObjectsRunsFieldsLinksAndUntouchedPackageParts()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "提案.pptx");
        CreatePresentation(source);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var service = new ArtifactTranslationService();
        var plan = await service.CreatePlanAsync(Request(temp.Path, source));

        Assert.Equal(ArtifactKind.PowerPointEnglishTranslation, plan.Kind);
        Assert.Equal(ArtifactFormat.PowerPointPresentation, plan.Format);
        Assert.Contains(plan.Text.Entries, entry => entry.SourceText == "提案概要" && entry.ShouldTranslate);
        Assert.Contains(plan.Text.Entries, entry => entry.SourceText == "表の説明" && entry.ShouldTranslate);
        Assert.Contains(plan.Text.Entries, entry => entry.SourceText == "ノートの説明" && entry.ShouldTranslate);
        Assert.Contains(plan.Text.Entries, entry => entry.SourceText == "マスターの見出し" && entry.ShouldTranslate);
        Assert.Contains(plan.Text.Entries, entry => entry.SourceText == "グラフの見出し" && entry.ShouldTranslate);
        Assert.Contains(plan.Text.Entries, entry => entry.SourceText == "図の説明" && entry.ShouldTranslate);
        Assert.Contains(plan.Text.Entries, entry => entry.SourceText == "日付" && !entry.ShouldTranslate);
        Assert.Contains(plan.Text.Entries, entry => entry.SourceText == "https://example.test" && !entry.ShouldTranslate);
        Assert.Contains(plan.Text.Entries, entry => entry.SourceText == "系列名" && !entry.ShouldTranslate);
        Assert.Contains(plan.Warnings, warning => warning.Contains("画像"));
        Assert.Contains(plan.Warnings, warning => warning.Contains("埋込"));
        Assert.Equal(plan.Text.Entries.Count, plan.Text.Entries.Select(entry => entry.Key).Distinct().Count());

        var translated = Values(plan);
        var prompt = new ArtifactPromptComposer(temp.Path).ComposeTextTranslationPrompt(plan,
            plan.Text.Entries.Where(entry => entry.ShouldTranslate).ToArray(), new ArtifactPromptContext());
        Assert.Contains("PowerPoint Presentation (.pptx)", prompt);
        Assert.Contains("slide", prompt);
        var result = await service.CreateArtifactAsync(plan, translated);
        Assert.True(result.Succeeded);
        Assert.Equal(translated.Count, result.TranslatedCount);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));

        using var original = ZipFile.OpenRead(source);
        using var output = ZipFile.OpenRead(result.OutputFilePath);
        Assert.Equal(original.Entries.Select(entry => entry.FullName).Order(), output.Entries.Select(entry => entry.FullName).Order());
        var changedParts = translated.Select(value => value.Key.Split(':')[1]).ToHashSet();
        foreach (var part in original.Entries)
        {
            var actual = output.GetEntry(part.FullName)!;
            if (!changedParts.Contains(part.FullName))
            {
                Assert.Equal(Bytes(part), Bytes(actual));
                continue;
            }
            using var oldStream = part.Open();
            using var newStream = actual.Open();
            var oldXml = XDocument.Load(oldStream);
            var newXml = XDocument.Load(newStream);
            // Text content is the only difference: geometry, styles, tables, hyperlinks,
            // animations, relationships, fields and numbers remain identical.
            foreach (var xml in new[] { oldXml, newXml })
                foreach (var text in xml.Descendants(XName.Get("t", DrawingNamespace)))
                    text.Value = "";
            Assert.True(XNode.DeepEquals(oldXml, newXml), part.FullName);
        }
        var outputPlan = await service.CreatePlanAsync(Request(temp.Path, result.OutputFilePath) with { OutputFileName = "Another.pptx" });
        Assert.All(translated, value => Assert.Contains(outputPlan.Text.Entries, entry => entry.SourceText == value.TranslatedText));
        using var reopened = PresentationDocument.Open(result.OutputFilePath, false);
        Assert.Single(reopened.PresentationPart!.SlideParts);
    }

    [Fact]
    public async Task Translation_InvalidMappingOrChangedSourceNeverCreatesOutput()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "proposal.pptx");
        CreatePresentation(source);
        var service = new ArtifactTranslationService();
        var plan = await service.CreatePlanAsync(Request(temp.Path, source));
        var values = Values(plan);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateArtifactAsync(plan, values.Skip(1).ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateArtifactAsync(plan,
            values.Select((value, index) => index == 0 ? value with { SourceText = "別原文" } : value).ToArray()));
        Assert.False(File.Exists(plan.OutputFullPath));
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(archive.CreateEntry("test.txt").Open()))
            writer.Write("source changed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateArtifactAsync(plan, values));
        Assert.False(File.Exists(plan.OutputFullPath));
    }

    [Fact]
    public async Task Translation_RejectsOverwriteAndInvalidOrSignedPresentation()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "proposal.pptx");
        CreatePresentation(source);
        var service = new ArtifactTranslationService();
        var plan = await service.CreatePlanAsync(Request(temp.Path, source));
        await File.WriteAllTextAsync(plan.OutputFullPath, "existing");
        await Assert.ThrowsAsync<IOException>(() => service.CreateArtifactAsync(plan, Values(plan)));
        Assert.Equal("existing", await File.ReadAllTextAsync(plan.OutputFullPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreatePlanAsync(Request(temp.Path, source) with { OutputFileName = "proposal.pptx" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreatePlanAsync(Request(temp.Path, source) with { OutputFileName = "proposal_EN.docx" }));
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Update))
            archive.CreateEntry("_xmlsignatures/sig1.xml");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreatePlanAsync(Request(temp.Path, source)));
        await File.WriteAllBytesAsync(source, [0, 1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreatePlanAsync(Request(temp.Path, source)));
    }

    private static ArtifactCreationRequest Request(string root, string source) => new()
    { CaseFolder = root, SourceFilePath = source, DestinationFolder = root, OutputFileName = "Proposal_EN.pptx" };

    private static IReadOnlyList<ArtifactTextTranslationValue> Values(ArtifactCreationPlan plan) => plan.Text.Entries
        .Where(entry => entry.ShouldTranslate).Select((entry, index) => new ArtifactTextTranslationValue
        { Key = entry.Key, SourceText = entry.SourceText, TranslatedText = $"English text {index}" }).ToArray();

    private static byte[] Bytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void CreatePresentation(string path)
    {
        using (var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            var slide = presentation.AddNewPart<SlidePart>();
            presentation.Presentation = new P.Presentation(new P.SlideIdList(new P.SlideId
            { Id = 256, RelationshipId = presentation.GetIdOfPart(slide) }));
            slide.AddHyperlinkRelationship(new Uri("https://example.test"), true, "link1");
            using var xml = new MemoryStream(Encoding.UTF8.GetBytes("""
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                 <p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>
                 <p:sp><p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="100000" cy="20000"/></a:xfrm></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/>
                  <a:p><a:pPr/><a:r><a:rPr b="1"/><a:t>提案</a:t></a:r><a:r><a:rPr b="1"/><a:t>概要</a:t></a:r><a:endParaRPr/></a:p>
                  <a:p><a:r><a:t>前半の説明</a:t></a:r><a:br/><a:r><a:t>後半の説明</a:t></a:r></a:p>
                  <a:p><a:r><a:rPr b="1"/><a:t>強調</a:t></a:r><a:r><a:rPr/><a:t>普通の文字</a:t></a:r></a:p>
                  <a:p><a:fld id="{11111111-1111-1111-1111-111111111111}" type="datetime1"><a:rPr/><a:t>日付</a:t></a:fld></a:p>
                  <a:p><a:r><a:rPr><a:hlinkClick r:id="link1"/></a:rPr><a:t>https://example.test</a:t></a:r></a:p>
                  <a:p><a:r><a:t>2026.2</a:t></a:r></a:p>
                 </p:txBody></p:sp>
                 <p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id="3" name="Table"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x="0" y="0"/><a:ext cx="100" cy="100"/></p:xfrm><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/table"><a:tbl><a:tblPr/><a:tblGrid><a:gridCol w="100"/></a:tblGrid><a:tr h="100"><a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>表の説明</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame>
                 </p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr><p:timing><p:tnLst><p:par><p:cTn id="1" dur="indefinite"/></p:par></p:tnLst></p:timing>
                </p:sld>
                """));
            slide.FeedData(xml);
        }
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        AddXml(archive, "ppt/notesSlides/notesSlide1.xml", "ノートの説明");
        AddXml(archive, "ppt/slideMasters/slideMaster1.xml", "マスターの見出し");
        AddXml(archive, "ppt/charts/chart1.xml", "グラフの見出し", "<c:strCache xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:pt idx=\"0\"><c:v>系列名</c:v></c:pt></c:strCache>");
        AddXml(archive, "ppt/diagrams/data1.xml", "図の説明");
        using (var image = archive.CreateEntry("ppt/media/image1.png").Open())
            image.Write([137, 80, 78, 71]);
        using (var embedded = archive.CreateEntry("ppt/embeddings/data.xlsx").Open())
            embedded.Write([1, 2, 3]);
    }

    private static void AddXml(ZipArchive archive, string name, string text, string extra = "")
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write($"<root xmlns:a=\"{DrawingNamespace}\"><a:p><a:r><a:t>{text}</a:t></a:r></a:p>{extra}</root>");
    }
}
