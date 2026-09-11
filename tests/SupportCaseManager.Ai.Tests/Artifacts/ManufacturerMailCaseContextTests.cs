using SupportCaseManager.Ai.Core.Artifacts;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class ManufacturerMailCaseContextTests
{
    [Fact]
    public void FollowUpPrompt_UsesResolvedCurrentAttachmentOnly()
    {
        var context = new ManufacturerMailCaseContext
        {
            SupportId = "00018742",
            ProductName = "Checkmarx",
            CurrentCustomerDeltaFileName = "追加問い合わせ内容.xlsx",
            CurrentCustomerDeltaContent =
            [
                "GetSecurePath、GetSecureName、GetSecureFileNameについて確認してください。",
                "IsCorrectPathでbasePathとcombinePathを別値にした場合もPath Traversalが検出される理由を確認したい。",
            ],
            CurrentOutboundAttachment = "Additional_Inquiry_Details_EN.xlsx",
            PreviousManufacturerContact = true,
            PreviousCustomerReply = true,
            CloseRequested = false,
            ManufacturerFollowupAllowed = true,
            CustomerReplyAllowed = false,
        };

        var prompt = new ArtifactPromptComposer().ComposeSimpleBilingualManufacturerMailPrompt(context);

        Assert.True(context.IsFollowUp);
        Assert.True(context.ManufacturerFollowupAllowed);
        Assert.False(context.CustomerReplyAllowed);
        Assert.Contains("Additional_Inquiry_Details_EN.xlsx", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Current Outbound Attachment: Inquiry_Details_EN.xlsx", prompt, StringComparison.Ordinal);
        Assert.Contains("CloseRequested: FALSE", prompt, StringComparison.Ordinal);
        Assert.Contains("GetSecurePath", prompt, StringComparison.Ordinal);
        Assert.Contains("basePath", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviousManufacturerResponse", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void FollowUpPrompt_DoesNotRequirePriorManufacturerResponseContent()
    {
        var context = new ManufacturerMailCaseContext
        {
            SupportId = "00018742",
            ProductName = "Checkmarx",
            CurrentCustomerDeltaFileName = "追加問い合わせ内容.xlsx",
            CurrentCustomerDeltaContent = ["追加質問について確認してください。"],
            CurrentOutboundAttachment = "Additional_Inquiry_Details_EN.xlsx",
            PreviousManufacturerContact = true,
            PreviousCustomerReply = true,
            ManufacturerFollowupAllowed = true,
            CustomerReplyAllowed = false,
        };

        Assert.True(context.IsFollowUp);
        Assert.True(context.CanGenerate);
    }

    [Fact]
    public void ReplyPrompt_UsesImmediateManufacturerResponseWithoutAttachmentOrQuestions()
    {
        var context = new ManufacturerMailCaseContext
        {
            CommunicationIntent = ManufacturerCommunicationIntent.ReplyToManufacturer,
            SupportId = "00018729",
            ProductName = "Klocwork",
            ImmediateManufacturerResponse = "Amazon Linux 2023 is supported up to 2023.8.\nJim Weber | Support Engineer",
            ImmediateManufacturerRecipientName = "Jim",
            CloseRequested = false,
            ProtectedValues = ManufacturerDraftPairParser.CreateCanonicalProtectedValueSet(
                "Klocwork", "00018729", string.Empty, [], "Amazon Linux 2023 is supported up to 2023.8."),
        };

        var prompt = new ArtifactPromptComposer().ComposeSimpleBilingualManufacturerMailPrompt(context);

        Assert.True(context.CanGenerateMail);
        Assert.Contains("Communication Intent: REPLY_TO_MANUFACTURER", prompt, StringComparison.Ordinal);
        Assert.Contains("Hi Jim", prompt, StringComparison.Ordinal);
        Assert.Contains("新しい技術質問、確認依頼", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Current Outbound Attachment:", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("本件をクローズしたいと考えています。")]
    [InlineData("If there are no particular issues, we would like to close this case.")]
    public void CloseIntentValidator_RecognizesUnsupportedCloseIntent(string draft)
    {
        Assert.True(ManufacturerMailConcepts.HasCloseRequest(draft));
    }

    [Fact]
    public void Sanitizer_RemovesCustomerPiiAndOldAttachments()
    {
        var lines = ManufacturerMailContentSanitizer.SanitizeLines(
            ["Test Company 山田太郎の連絡先 taro@example.com、内線: 1234。Inquiry_Details_EN.xlsxを参照してください。"],
            "Test Company",
            "山田太郎",
            "Additional_Inquiry_Details_EN.xlsx");

        var sanitized = Assert.Single(lines);
        Assert.DoesNotContain("Test Company", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("山田太郎", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("taro@example.com", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Inquiry_Details_EN.xlsx", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedValueParity_UsesCanonicalSetWithoutFixedCount()
    {
        var protectedValues = ProtectedValues("GetSecurePath", "GetSecureName", "Additional_Inquiry_Details_EN.xlsx");
        var context = new ManufacturerMailCaseContext
        {
            SupportId = "00018742",
            ProductName = "Checkmarx",
            CurrentCustomerDeltaFileName = "追加問い合わせ内容.xlsx",
            CurrentCustomerDeltaContent = ["GetSecurePath と GetSecureName を確認してください。"],
            CurrentOutboundAttachment = "Additional_Inquiry_Details_EN.xlsx",
            ManufacturerFollowupAllowed = true,
            ProtectedValues = protectedValues,
        };

        var prompt = new ArtifactPromptComposer().ComposeSimpleBilingualManufacturerMailPrompt(context);
        var injection = protectedValues.EvaluatePromptInjection(prompt);
        var validation = new ManufacturerDraftPairParser().Validate(
            new ManufacturerDraftPair
            {
                JapaneseDraft = "GetSecureName、Additional_Inquiry_Details_EN.xlsx、GetSecurePath",
                EnglishDraft = "Additional_Inquiry_Details_EN.xlsx, GetSecurePath, GetSecureName",
            },
            protectedValues,
            ["Additional_Inquiry_Details_EN.xlsx"]);
        var parity = protectedValues.EvaluateDraftParity(prompt, validation);

        Assert.Equal(protectedValues.Count, injection.ExpectedCount);
        Assert.Equal(injection.ExpectedCount, injection.InjectedCount);
        Assert.Equal(0, parity.MissingCount);
        Assert.True(parity.IsComplete);
    }

    [Fact]
    public void ProtectedValueParity_00018742DerivesThirteenValuesFromCaseInput()
    {
        var protectedValues = ManufacturerDraftPairParser.CreateCanonicalProtectedValueSet(
            "Checkmarx",
            "00018742",
            "Additional_Inquiry_Details_EN.xlsx",
            ["Additional_Inquiry_Details_EN.xlsx"],
            "GetSecurePath GetSecureName GetSecureFileName IsCorrectPath Path.GetFileName() basePath combinePath 9.7.4.1008 HF9 KSAY0005207");
        var context = new ManufacturerMailCaseContext
        {
            SupportId = "00018742",
            ProductName = "Checkmarx",
            CurrentCustomerDeltaFileName = "追加問い合わせ内容.xlsx",
            CurrentCustomerDeltaContent = ["GetSecurePath と IsCorrectPath の追加確認です。"],
            CurrentOutboundAttachment = "Additional_Inquiry_Details_EN.xlsx",
            ManufacturerFollowupAllowed = true,
            ProtectedValues = protectedValues,
        };
        var prompt = new ArtifactPromptComposer().ComposeSimpleBilingualManufacturerMailPrompt(context);
        var text = string.Join(" ", protectedValues.Values);
        var validation = new ManufacturerDraftPairParser().Validate(
            new ManufacturerDraftPair { JapaneseDraft = text, EnglishDraft = text },
            protectedValues,
            ["Additional_Inquiry_Details_EN.xlsx"]);
        var parity = protectedValues.EvaluateDraftParity(prompt, validation);

        Assert.Equal(13, protectedValues.Count);
        Assert.Equal(protectedValues.Count, parity.ExpectedCount);
        Assert.Equal(parity.ExpectedCount, parity.InjectedCount);
        Assert.Equal(0, parity.MissingCount);
        Assert.True(parity.IsComplete);
    }

    [Fact]
    public void ProtectedValueParity_FailsClosedWhenPromptInjectionIsIncomplete()
    {
        var protectedValues = ProtectedValues("GetSecurePath", "GetSecureName");

        var parity = protectedValues.EvaluatePromptInjection("GetSecurePath");

        Assert.Equal(2, parity.ExpectedCount);
        Assert.Equal(1, parity.InjectedCount);
        Assert.False(parity.PromptParity);
        Assert.False(parity.IsComplete);
    }

    [Fact]
    public void ProtectedValueParity_FailsClosedWhenDraftValuesAreMissing()
    {
        var protectedValues = ProtectedValues("GetSecurePath", "GetSecureName");
        const string prompt = "GetSecurePath GetSecureName";
        var validation = new ManufacturerDraftPairParser().Validate(
            new ManufacturerDraftPair
            {
                JapaneseDraft = "GetSecurePath",
                EnglishDraft = "GetSecurePath",
            },
            protectedValues,
            []);

        var parity = protectedValues.EvaluateDraftParity(prompt, validation);

        Assert.Equal(2, parity.ExpectedCount);
        Assert.Equal(2, parity.InjectedCount);
        Assert.Equal(1, parity.MissingCount);
        Assert.False(parity.IsComplete);
    }

    private static ManufacturerProtectedValueSet ProtectedValues(params string[] values) => new()
    {
        Items = values.Select(value => new ManufacturerProtectedValue
        {
            Literal = value,
            Category = "Technical",
        }).ToArray(),
    };
}
