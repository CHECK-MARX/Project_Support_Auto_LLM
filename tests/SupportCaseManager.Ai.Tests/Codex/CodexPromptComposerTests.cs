using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Codex;

public sealed class CodexPromptComposerTests
{
    [Fact]
    public void ComposeInitialPrompt_UsesRequiredSectionOrderAndProductInstructions()
    {
        using var temp = new TempDirectory();
        var promptRoot = Path.Combine(temp.Path, "prompts");
        Directory.CreateDirectory(Path.Combine(promptRoot, "products"));
        File.WriteAllText(Path.Combine(promptRoot, "common-support-rules.txt"), "COMMON");
        File.WriteAllText(Path.Combine(promptRoot, "products", "qac.txt"), "PRODUCT");
        var composer = new CodexPromptComposer(temp.Path);

        var result = composer.ComposeInitialPrompt(new CodexInitialPromptContext
        {
            ProductName = "HelixQAC",
            ProductPromptFilePath = "prompts/products/qac.txt",
            SupportId = "0001",
            InquiryText = "INQUIRY",
            Attachments = [new CodexPromptAttachment("trace.log", CodexCaseFileKind.Log, 10)],
            AttachmentContents = [new CodexReadableAttachmentContent("trace.log", "Log", "Shift-JIS/CP932", "LOG_BODY", false)],
            Evidence = [new SearchSource { Title = "EVIDENCE", SourceType = "CuratedFacts", Text = "FACT" }],
            UserInstruction = "USER",
        });

        var expectedOrder = new[] { "COMMON", "PRODUCT", "## 3. 案件情報", "INQUIRY", "trace.log", "LOG_BODY", "EVIDENCE", "USER" };
        var previous = -1;
        foreach (var value in expectedOrder)
        {
            var index = result.Prompt.IndexOf(value, StringComparison.Ordinal);
            Assert.True(index > previous, $"'{value}' was not in the required order.");
            previous = index;
        }
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ComposeFollowUpPrompt_IncludesNewlyNormalizedAttachmentContent()
    {
        var result = new CodexPromptComposer().ComposeFollowUpPrompt(
            "ログを再確認してください。",
            [new CodexReadableAttachmentContent("trace.log", "Log", "UTF-8", "ERROR detail", true)]);

        Assert.Contains("ログを再確認してください。", result);
        Assert.Contains("trace.log", result);
        Assert.Contains("ERROR detail", result);
        Assert.Contains("UTF-8", result);
    }

    [Fact]
    public void ComposeInitialPrompt_MissingProductFileWarnsAndContinues()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "prompts"));
        File.WriteAllText(Path.Combine(temp.Path, "prompts", "common-support-rules.txt"), "COMMON");

        var result = new CodexPromptComposer(temp.Path).ComposeInitialPrompt(new CodexInitialPromptContext
        {
            ProductPromptFilePath = "prompts/products/missing.txt",
            UserInstruction = "調査",
        });

        Assert.Contains("COMMON", result.Prompt);
        Assert.Contains(result.Warnings, warning => warning.Contains("missing.txt"));
    }
}
