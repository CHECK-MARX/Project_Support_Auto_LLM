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

    [Fact]
    public void ComposeInitialPrompt_WithRagLabEvidence_AddsIndependentSectionBeforeUserInstruction()
    {
        var result = new CodexPromptComposer().ComposeInitialPrompt(new CodexInitialPromptContext
        {
            Evidence = [new SearchSource { Title = "既存RAG", SourceType = "Manual", Text = "既存根拠" }],
            RagLabEvidence =
            [
                new RagLabEvidenceItem
                {
                    SourceType = "OfficialDoc",
                    DocumentId = "doc-1",
                    Product = "Checkmarx",
                    Version = "1.0",
                    Score = 0.875,
                    SelectionReason = "人工選定理由",
                    Warnings = ["人工警告"],
                    Text = "追加根拠本文",
                },
            ],
            UserInstruction = "今回の指示",
        });

        var existingIndex = result.Prompt.IndexOf("既存根拠", StringComparison.Ordinal);
        var ragStartIndex = result.Prompt.IndexOf("[RAG Evidence]", StringComparison.Ordinal);
        var ragEndIndex = result.Prompt.IndexOf("[End RAG Evidence]", StringComparison.Ordinal);
        var instructionIndex = result.Prompt.LastIndexOf("今回の指示", StringComparison.Ordinal);
        Assert.True(existingIndex < ragStartIndex);
        Assert.True(ragStartIndex < ragEndIndex);
        Assert.True(ragEndIndex < instructionIndex);
        Assert.Contains("Document ID: doc-1", result.Prompt);
        Assert.Contains("Warnings: 人工警告", result.Prompt);
        Assert.Contains("追加根拠本文", result.Prompt);
        Assert.Contains("公式情報を最優先", result.Prompt);
        Assert.Contains("お客様向け回答には、RAG、Evidence、スコア", result.Prompt);
    }

    [Fact]
    public void ComposeInitialPrompt_WithoutRagLabEvidence_RemainsUnchanged()
    {
        var composer = new CodexPromptComposer();
        var originalContext = new CodexInitialPromptContext
        {
            ProductName = "HelixQAC",
            InquiryText = "問い合わせ",
            Evidence = [new SearchSource { Title = "既存根拠", SourceType = "Manual", Text = "本文" }],
            UserInstruction = "調査してください",
        };

        var defaultResult = composer.ComposeInitialPrompt(originalContext);
        var explicitEmptyResult = composer.ComposeInitialPrompt(originalContext with { RagLabEvidence = [] });

        Assert.Equal(defaultResult.Prompt, explicitEmptyResult.Prompt);
        Assert.DoesNotContain("[RAG Evidence]", defaultResult.Prompt, StringComparison.Ordinal);
    }
}
