using SupportCaseManager.Ai.Core.Artifacts;
using SupportCaseManager.Ai.Core.Codex;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class ManufacturerInquiryWorkflowTests
{
    [Fact]
    public async Task TranslationSelectionRequiresExplicitSafeUnchangedSourceAndOutput()
    {
        var root = Directory.CreateTempSubdirectory("manufacturer-selection-");
        var outside = Path.GetTempFileName();
        try
        {
            var source = Path.Combine(root.FullName, "質問.xlsx");
            var output = Path.Combine(root.FullName, "Questions_EN.xlsx");
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllBytesAsync(output, [2]);
            var selection = ManufacturerTranslationSelection.Create(root.FullName, source, output);
            Assert.True(selection.IsValid(root.FullName));
            Assert.Throws<InvalidOperationException>(() => ManufacturerTranslationSelection.Create(root.FullName, source, source));
            Assert.ThrowsAny<Exception>(() => ManufacturerTranslationSelection.Create(root.FullName, source, outside));
            await File.WriteAllBytesAsync(output, [3]);
            Assert.False(selection.IsValid(root.FullName));
        }
        finally { root.Delete(true); File.Delete(outside); }
    }

    [Fact]
    public async Task AttachmentsAreCurrentSafeFilesWithTranslationReplacementAndMissingNameWarning()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        try
        {
            var bundle = Directory.CreateDirectory(Path.Combine(root, "お客様ご相談内容_20261006_0001", "資料")).FullName;
            foreach (var name in new[] { "questions.xlsx", "questions_EN.xlsx", "report_2.pdf", "source.zip", "query.xml", "obsolete_EN.xlsx" })
                await File.WriteAllBytesAsync(Path.Combine(bundle, name), [1]);
            var old = Directory.CreateDirectory(Path.Combine(root, "メーカー連携内容_20250101")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(old, "old.pdf"), [1]);
            var files = (await new CodexCaseFileScanner().ScanAsync(root)).Files;
            var result = ManufacturerAttachmentResolver.Resolve(root, Path.Combine(root, "お客様ご相談内容_0001.txt"),
                "・report.pdf\n・source.zip\n・query.xml\n・questions.xlsx", Path.Combine(bundle, "questions.xlsx"),
                Path.Combine(bundle, "questions_EN.xlsx"), files);
            Assert.Equal(4, result.Files.Count);
            Assert.Contains(result.Files, file => file.FileName == "report_2.pdf");
            Assert.DoesNotContain(result.Files, file => file.FileName is "old.pdf" or "questions.xlsx" or "obsolete_EN.xlsx");
            Assert.Single(result.Warnings);
            Assert.Contains("report.pdf", result.Warnings[0]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InitialPromptDoesNotRequireFollowUpAndProtectsAllAttachments()
    {
        var context = new ManufacturerMailCaseContext {
            ProductName = "ExampleProduct", SupportId = "0001", CustomerReplyAllowed = true,
            CurrentCustomerDeltaContent = ["対象の比較方法を教えてください。"],
            CurrentOutboundAttachments = ["report.pdf", "code.zip"],
            ProtectedValues = ManufacturerDraftPairParser.CreateCanonicalProtectedValueSet("ExampleProduct", "0001", "report.pdf", ["report.pdf", "code.zip"]) };
        var prompt = new ArtifactPromptComposer().ComposeSimpleBilingualManufacturerMailPrompt(context);
        Assert.True(context.CanGenerateMail);
        Assert.Contains("初回の確認依頼", prompt);
        Assert.Contains("report.pdf, code.zip", prompt);
        Assert.DoesNotContain("挨拶、Follow-upの経緯", prompt);
        Assert.Contains("code.zip", context.ProtectedValues.Values);
        Assert.Contains("説明文・項目名はすべて英語", prompt);
    }

    [Theory]
    [InlineData("Thank you for your previous response.", false)]
    [InlineData("Customer reference: 受付番号 ABC123", false)]
    [InlineData("Please review 問い合わせ内容.xlsx.", true)]
    [InlineData("Customer reference: ABC123", true)]
    public void CommunicationValidationRejectsInventedHistoryAndJapaneseLabels(string english, bool valid)
    {
        var errors = new ManufacturerDraftPairParser().ValidateCommunication(new() { EnglishDraft = english },
            new() { CurrentOutboundAttachments = ["問い合わせ内容.xlsx"] });
        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void SanitizerKeepsOnlyAllowedAttachmentNamesAndRemovesContacts()
    {
        var lines = ManufacturerMailContentSanitizer.SanitizeLines(["Customer Ltd contact: person@example.com; report.pdf, code.zip, old.pdf"],
            "Customer Ltd", "", ["report.pdf", "code.zip"]);
        Assert.Contains("report.pdf", lines[0]);
        Assert.Contains("code.zip", lines[0]);
        Assert.DoesNotContain("old.pdf", lines[0]);
        Assert.DoesNotContain("person@example.com", lines[0]);
    }
}
