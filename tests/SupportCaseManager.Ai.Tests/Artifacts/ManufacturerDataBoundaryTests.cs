using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Artifacts;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class ManufacturerDataBoundaryTests
{
    [Fact]
    public void Validator_AllowsCurrentAttachmentInNaturalProse()
    {
        var validation = ValidateSafeDraft(
            "追加の確認事項をAdditional_Inquiry_Details_EN.xlsxに記載しています。",
            "The additional questions are in Additional_Inquiry_Details_EN.xlsx for your review.");

        Assert.True(validation.Succeeded);
        Assert.True(validation.AttachmentParity);
        Assert.True(validation.DataBoundary);
    }

    [Fact]
    public void Validator_AllowsCurrentAttachmentAfterAttachmentLabel()
    {
        var validation = ValidateSafeDraft(
            "添付: Additional_Inquiry_Details_EN.xlsx",
            "Attachment: Additional_Inquiry_Details_EN.xlsx");

        Assert.True(validation.Succeeded);
    }

    [Fact]
    public void Validator_AllowsCurrentAttachmentWithPunctuation()
    {
        var validation = ValidateSafeDraft(
            "Additional_Inquiry_Details_EN.xlsxをご確認ください。",
            "Please review Additional_Inquiry_Details_EN.xlsx.");

        Assert.True(validation.Succeeded);
    }

    [Fact]
    public void Validator_DoesNotTreatHistoricalSuffixAsAttachment()
    {
        var validation = new ManufacturerDraftPairParser().Validate(
            new ManufacturerDraftPair
            {
                JapaneseDraft = "添付: (Additional_Inquiry_Details_EN.xlsx)",
                EnglishDraft = "Attachment: \"Additional_Inquiry_Details_EN.xlsx\"",
            },
            new ManufacturerProtectedValueSet
            {
                Items = [new() { Literal = "Additional_Inquiry_Details_EN.xlsx", Category = "Attachment" }],
            },
            ["Additional_Inquiry_Details_EN.xlsx"],
            new ManufacturerDraftBoundaryContext
            {
                ProhibitedAttachmentNames = ["Inquiry_Details_EN.xlsx"],
            });

        Assert.True(validation.Succeeded);
        Assert.DoesNotContain(validation.DataBoundaryViolations, value => value.Contains("Inquiry_Details_EN.xlsx", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsHistoricalAttachmentWhenItIsAnIndependentToken()
    {
        var validation = new ManufacturerDraftPairParser().Validate(
            new ManufacturerDraftPair
            {
                JapaneseDraft = "添付: Additional_Inquiry_Details_EN.xlsx、Inquiry_Details_EN.xlsx",
                EnglishDraft = "Attachments: Additional_Inquiry_Details_EN.xlsx and Inquiry_Details_EN.xlsx",
            },
            new ManufacturerProtectedValueSet
            {
                Items = [new() { Literal = "Additional_Inquiry_Details_EN.xlsx", Category = "Attachment" }],
            },
            ["Additional_Inquiry_Details_EN.xlsx"],
            new ManufacturerDraftBoundaryContext
            {
                ProhibitedAttachmentNames = ["Inquiry_Details_EN.xlsx"],
            });

        Assert.False(validation.Succeeded);
        Assert.Contains("PriorAttachment:Inquiry_Details_EN.xlsx", validation.DataBoundaryViolations);
    }

    [Fact]
    public void Validator_RejectsPriorPdfMention()
    {
        var validation = ValidateSafeDraft(
            "添付: Additional_Inquiry_Details_EN.xlsx、KSAY0005207.pdf",
            "Attachments: Additional_Inquiry_Details_EN.xlsx and KSAY0005207.pdf");

        Assert.False(validation.Succeeded);
        Assert.Contains("PriorAttachment:KSAY0005207.pdf", validation.DataBoundaryViolations);
    }

    [Fact]
    public void Validator_RejectsPriorZipMention()
    {
        var validation = ValidateSafeDraft(
            "添付: Additional_Inquiry_Details_EN.xlsx、KSAY0005207.zip",
            "Attachments: Additional_Inquiry_Details_EN.xlsx and KSAY0005207.zip");

        Assert.False(validation.Succeeded);
        Assert.Contains("PriorAttachment:KSAY0005207.zip", validation.DataBoundaryViolations);
    }

    [Fact]
    public void Validator_DoesNotCreateConcatenatedFilenameFromProse()
    {
        var validation = ValidateSafeDraft(
            "資料をAdditional_Inquiry_Details_EN.xlsxとして添付します。",
            "The materials designated for this submission are Additional_Inquiry_Details_EN.xlsx.");

        Assert.True(validation.Succeeded);
        Assert.DoesNotContain(
            validation.DataBoundaryViolations,
            value => value.StartsWith("Attachment:The materials", StringComparison.Ordinal));
    }

    [Fact]
    public void ManufacturerDraft_DoesNotLeakCustomerPersonName()
    {
        var prompt = BuildSafePrompt("Question 1: Please review the current result. Customer: Jane Doe");

        Assert.DoesNotContain("Jane Doe", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManufacturerDraft_DoesNotLeakCustomerExtension()
    {
        var prompt = BuildSafePrompt("Question 1: Please review the current result. 内線: 368506");

        Assert.DoesNotContain("368506", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("内線", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ManufacturerDraft_DoesNotLeakCustomerEmail()
    {
        var prompt = BuildSafePrompt("Question 1: Please review the current result. jane.doe@example.com");

        Assert.DoesNotContain("jane.doe@example.com", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManufacturerDraft_DoesNotLeakCustomerCompanyByDefault()
    {
        var prompt = BuildSafePrompt("Question 1: Please review the current result. Acme Customer");

        Assert.DoesNotContain("Acme Customer", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManufacturerDraft_DoesNotLeakInternalRuntimeState()
    {
        var prompt = BuildSafePrompt(
            "Question 1: Please review Path.GetFileName(). CurrentCase Evidence: internal; translation target elements: 0");

        Assert.Contains("Question 1", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentCase Evidence", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("translation target", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManufacturerDraft_DoesNotListPriorAttachments()
    {
        var prompt = BuildSafePrompt(
            "Question 1: Please review the current result. Prior file: KSAY0005207.csv");

        Assert.Contains("Additional_Inquiry_Details_EN.xlsx", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("KSAY0005207.csv", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafeContext_UsesAllowListAndDoesNotLeakCustomerOrInternalState()
    {
        var scope = new ManufacturerFollowUpScope
        {
            Mode = ManufacturerDraftMode.FollowUp,
            CurrentCustomerDelta =
            [
                Source(
                    "delta",
                    "追加問い合わせ内容.xlsx",
                    "Customer: Acme Customer; Jane Doe; 内線: 368506; jane.doe@example.com\n"
                    + "Question 1: Please review Path.GetFileName() and KSAY0005207.\n"
                    + "Prior file: KSAY0005207.csv; translation target elements: 0; CurrentCase Evidence: internal"),
            ],
            PriorManufacturerResponse =
            [
                Source(
                    "response",
                    "メーカー連携内容_00018742_reply.txt",
                    "From: John Smith <john.smith@manufacturer.example>\n"
                    + "Thank you for your question. Please send the current result.")
            ],
            CurrentOutboundAttachments = ["Additional_Inquiry_Details_EN.xlsx"],
        };

        var safe = ManufacturerSafeContextFactory.Create(
            "Checkmarx",
            "9.7.6",
            "00018742",
            "Acme Customer",
            "Jane Doe",
            new ManufacturerRecipient
            {
                DisplayName = "John Smith",
                EmailAddress = "john.smith@manufacturer.example",
                ResolutionStatus = "RESOLVED",
            },
            scope,
            "Additional_Inquiry_Details_EN.xlsx");
        var prompt = new ArtifactPromptComposer().ComposeBilingualManufacturerMailPrompt(
            "Excel Workbook (.xlsx)",
            "Additional_Inquiry_Details_EN.xlsx",
            "- Sheet1!A1: Question 1",
            new ArtifactPromptContext { ManufacturerSafeContext = safe },
            scope.CurrentOutboundAttachments);

        Assert.Contains("Checkmarx", prompt, StringComparison.Ordinal);
        Assert.Contains("9.7.6", prompt, StringComparison.Ordinal);
        Assert.Contains("00018742", prompt, StringComparison.Ordinal);
        Assert.Contains("Additional_Inquiry_Details_EN.xlsx", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Acme Customer", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Jane Doe", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("368506", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("jane.doe@example.com", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KSAY0005207.csv", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CurrentCase Evidence", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("translation target", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("john.smith@manufacturer.example", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_RejectsCustomerAndInternalBoundaryViolations()
    {
        var validation = new ManufacturerDraftPairParser().Validate(
            new ManufacturerDraftPair
            {
                JapaneseDraft = "質問1: Acme Customer、Jane Doe、内線: 368506。添付: Additional_Inquiry_Details_EN.xlsx。",
                EnglishDraft = "Question 1: Acme Customer, Jane Doe, extension: 368506. Attachment: Additional_Inquiry_Details_EN.xlsx. CurrentCase Evidence",
            },
            new ManufacturerProtectedValueSet
            {
                Items = [new() { Literal = "Additional_Inquiry_Details_EN.xlsx", Category = "Attachment" }],
            },
            ["Additional_Inquiry_Details_EN.xlsx"],
            new ManufacturerDraftBoundaryContext
            {
                CustomerPersonName = "Jane Doe",
                CustomerCompanyName = "Acme Customer",
                ProhibitedAttachmentNames = ["KSAY0005207.csv"],
            });

        Assert.False(validation.Succeeded);
        Assert.False(validation.DataBoundary);
        Assert.Contains("CustomerPersonName", validation.DataBoundaryViolations);
        Assert.Contains("CustomerCompanyName", validation.DataBoundaryViolations);
        Assert.Contains("CustomerExtension", validation.DataBoundaryViolations);
        Assert.Contains("Internal:CurrentCase Evidence", validation.DataBoundaryViolations);
    }

    private static SearchSource Source(string id, string title, string text) => new()
    {
        SourceId = id,
        SourceType = "CurrentCase",
        Title = title,
        Text = text,
        SourceRole = id == "response" ? "PriorManufacturerResponse" : "CurrentCustomerDelta",
    };

    private static string BuildSafePrompt(string currentDelta)
    {
        var scope = new ManufacturerFollowUpScope
        {
            Mode = ManufacturerDraftMode.FollowUp,
            CurrentCustomerDelta = [Source("delta", "追加問い合わせ内容.xlsx", currentDelta)],
            CurrentOutboundAttachments = ["Additional_Inquiry_Details_EN.xlsx"],
        };
        var safe = ManufacturerSafeContextFactory.Create(
            "Checkmarx",
            "9.7.6",
            "00018742",
            "Acme Customer",
            "Jane Doe",
            new ManufacturerRecipient { DisplayName = "John Smith", ResolutionStatus = "RESOLVED" },
            scope,
            "Additional_Inquiry_Details_EN.xlsx");

        return new ArtifactPromptComposer().ComposeBilingualManufacturerMailPrompt(
            "Excel Workbook (.xlsx)",
            "Additional_Inquiry_Details_EN.xlsx",
            string.Empty,
            new ArtifactPromptContext { ManufacturerSafeContext = safe },
            scope.CurrentOutboundAttachments);
    }

    private static ManufacturerDraftValidationResult ValidateSafeDraft(
        string japaneseDraft,
        string englishDraft)
    {
        return new ManufacturerDraftPairParser().Validate(
            new ManufacturerDraftPair
            {
                JapaneseDraft = $"質問1: {japaneseDraft}",
                EnglishDraft = $"Question 1: {englishDraft}",
            },
            new ManufacturerProtectedValueSet
            {
                Items =
                [
                    new()
                    {
                        Literal = "Additional_Inquiry_Details_EN.xlsx",
                        Category = "Attachment",
                    },
                ],
            },
            ["Additional_Inquiry_Details_EN.xlsx"],
            new ManufacturerDraftBoundaryContext
            {
                CustomerPersonName = "Jane Doe",
                CustomerCompanyName = "Acme Customer",
                ProhibitedAttachmentNames = ["KSAY0005207.pdf", "KSAY0005207.zip"],
            });
    }
}
