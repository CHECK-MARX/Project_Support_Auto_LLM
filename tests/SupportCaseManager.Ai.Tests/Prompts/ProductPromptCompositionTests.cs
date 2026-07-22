using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Prompts;

namespace SupportCaseManager.Ai.Tests.Prompts;

public sealed class ProductPromptCompositionTests
{
    [Fact]
    public void Build_ComposesInstructionsAndCaseInputsInRequiredOrder()
    {
        var request = new AnswerDraftRequest
        {
            CommonInstruction = "COMMON_INSTRUCTION_MARKER",
            ProductInstruction = "PRODUCT_INSTRUCTION_MARKER",
            Case = new CaseContext
            {
                ProductName = "PRODUCT_CASE_MARKER",
                CompanyName = "COMPANY_CASE_MARKER",
                SupportNumber = "SUPPORT_CASE_MARKER",
            },
            InquiryText = "INQUIRY_MARKER",
            AttachmentFileNames = ["ATTACHMENT_MARKER.pdf"],
            UserInstruction = "USER_INSTRUCTION_MARKER",
            Settings = new AiAssistantSettings
            {
                MaxPromptChars = 20_000,
                MaxEvidenceItems = 3,
            },
        };

        var messages = new PromptBuilder().Build(request);

        AssertOrdered(messages.SystemPrompt, "COMMON_INSTRUCTION_MARKER", "PRODUCT_INSTRUCTION_MARKER");
        AssertOrdered(
            messages.UserPrompt,
            "PRODUCT_CASE_MARKER",
            "INQUIRY_MARKER",
            "ATTACHMENT_MARKER.pdf",
            "USER_INSTRUCTION_MARKER");
    }

    private static void AssertOrdered(string text, params string[] markers)
    {
        var previousIndex = -1;
        foreach (var marker in markers)
        {
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"'{marker}' was missing or out of order.");
            previousIndex = index;
        }
    }
}
