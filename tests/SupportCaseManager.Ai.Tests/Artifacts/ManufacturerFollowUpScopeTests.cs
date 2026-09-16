using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Artifacts;
using SupportCaseManager.Ai.Core.Codex;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class ManufacturerFollowUpScopeTests
{
    [Fact]
    public void Resolve_FollowUpSeparatesDeltaResponseAndPriorAttachments()
    {
        SearchSource[] evidence =
        [
            Source(
                "prior-response",
                "メーカー連携内容_00018742_reply.txt",
                "メーカー回答: Path Traversalの23件について回答済みです。",
                "PriorManufacturerResponse"),
            Source(
                "prior-request",
                "メーカー連携内容_00018742_request.txt",
                "メーカーへの質問: Path Traversalの23件を確認してください。",
                "PriorManufacturerRequest"),
            Source(
                "delta",
                "追加問い合わせ内容.xlsx",
                "お客様の追加質問: Question 1. 追加の確認事項です。",
                "CurrentCustomerDelta"),
            Source(
                "old-csv",
                "KSAY0005207.csv",
                "old submitted findings",
                "PriorSubmissionAttachment"),
            Source(
                "old-xml",
                "MHI.xml",
                "old submitted metadata",
                "PriorSubmissionAttachment"),
            Source(
                "background",
                "case-summary.txt",
                "Checkmarx version 9.7.4.1008 HF9",
                "CaseBackground"),
            Source(
                "internal",
                "diagnostic.txt",
                "translation target element = 0; no selected CurrentCase Evidence",
                "InternalRuntimeState"),
            Source(
                "generated",
                "Additional_Inquiry_Details_EN.xlsx",
                "generated artifact from an earlier run",
                "GeneratedArtifact"),
        ];
        CodexCaseFileInfo[] files =
        [
            FileInfo("追加問い合わせ内容.xlsx", CodexCaseFileKind.CustomerInquiry),
            FileInfo("KSAY0005207.csv", CodexCaseFileKind.Document),
            FileInfo("MHI.xml", CodexCaseFileKind.Configuration),
        ];

        var scope = ManufacturerFollowUpScopeResolver.Resolve(
            evidence,
            files,
            ["追加問い合わせ内容.xlsx", "KSAY0005207.csv", "MHI.xml", "Additional_Inquiry_Details_EN.xlsx"],
            "Additional_Inquiry_Details_EN.xlsx");

        Assert.Equal(ManufacturerDraftMode.FollowUp, scope.Mode);
        Assert.True(scope.HasPriorManufacturerResponse);
        Assert.Contains(scope.CurrentCustomerDelta, item => item.SourceId == "delta");
        Assert.Contains(scope.PriorManufacturerResponse, item => item.SourceId == "prior-response");
        Assert.Contains(scope.PriorManufacturerRequest, item => item.SourceId == "prior-request");
        Assert.Contains(scope.GeneratedArtifacts, item => item.SourceId == "generated");
        Assert.Equal(["Additional_Inquiry_Details_EN.xlsx"], scope.CurrentOutboundAttachments);
        Assert.Contains("KSAY0005207.csv", scope.PriorSubmissionAttachmentsExcluded);
        Assert.Contains("MHI.xml", scope.PriorSubmissionAttachmentsExcluded);
        Assert.DoesNotContain(scope.PromptEvidence, item => item.SourceId is "old-csv" or "old-xml" or "prior-request" or "generated" or "internal");
        Assert.Contains(scope.PromptEvidence, item => item.SourceId == "delta");
        Assert.Contains(scope.PromptEvidence, item => item.SourceId == "prior-response");
        Assert.Contains(scope.PromptEvidence, item => item.SourceId == "background");
        Assert.Equal(1, scope.InternalStateExcludedCount);
    }

    [Fact]
    public void Resolve_InitialDoesNotPromoteSelectedEvidenceToOutboundAttachments()
    {
        var scope = ManufacturerFollowUpScopeResolver.Resolve(
            [
                Source("current", "問い合わせ内容.xlsx", "初回質問です。", "CurrentCustomerDelta"),
            ],
            [FileInfo("問い合わせ内容.xlsx", CodexCaseFileKind.CustomerInquiry)],
            ["問い合わせ内容.xlsx"],
            "問い合わせ内容_EN.xlsx");

        Assert.Equal(ManufacturerDraftMode.Initial, scope.Mode);
        Assert.False(scope.HasPriorManufacturerResponse);
        Assert.Equal(
            ["問い合わせ内容_EN.xlsx"],
            scope.CurrentOutboundAttachments);
        Assert.Equal(["問い合わせ内容.xlsx"], scope.PriorSubmissionAttachmentsExcluded);
    }

    [Fact]
    public void Resolve_FollowUpCanUseWorkflowEvidenceWithoutManufacturerResponseBody()
    {
        var scope = ManufacturerFollowUpScopeResolver.Resolve(
            [
                Source("request", "メーカー連携内容_00018742.txt", "メーカーへの確認依頼です。", "PriorManufacturerRequest"),
                Source("reply", "お客様への返信案_00018742.txt", "お客様へ前回結果を回答しました。", "PreviousCustomerReply"),
                Source("delta", "追加問い合わせ内容.xlsx", "お客様の追加質問: Question 1", "CurrentCustomerDelta"),
            ],
            [
                FileInfo("メーカー連携内容_00018742.txt", CodexCaseFileKind.CustomerInquiry),
                FileInfo("お客様への返信案_00018742.txt", CodexCaseFileKind.CustomerInquiry),
                FileInfo("追加問い合わせ内容.xlsx", CodexCaseFileKind.CustomerInquiry),
            ],
            ["Additional_Inquiry_Details_EN.xlsx"],
            "Additional_Inquiry_Details_EN.xlsx");

        Assert.Equal(ManufacturerDraftMode.FollowUp, scope.Mode);
        Assert.False(scope.HasPriorManufacturerResponse);
        Assert.True(scope.PreviousManufacturerContactConfirmed);
        Assert.True(scope.PreviousCustomerReplyFound);
        Assert.Contains(scope.PromptEvidence, item => item.SourceId == "delta");
        Assert.DoesNotContain(scope.PromptEvidence, item => item.SourceId == "reply");
    }

    [Fact]
    public void FollowUpPromptUsesDeltaAndCurrentOutboundAttachmentOnly()
    {
        var scope = ManufacturerFollowUpScopeResolver.Resolve(
            [
                Source("response", "manufacturer-response.txt", "前回回答済みです。", "PriorManufacturerResponse"),
                Source("delta", "追加問い合わせ内容.xlsx", "お客様の追加質問: Question 1", "CurrentCustomerDelta"),
                Source("old", "KSAY0005207.csv", "以前送付した資料", "PriorSubmissionAttachment"),
                Source("internal", "diagnostic.txt", "translation target element = 0", "InternalRuntimeState"),
            ],
            [FileInfo("追加問い合わせ内容.xlsx", CodexCaseFileKind.CustomerInquiry)],
            ["追加問い合わせ内容.xlsx", "KSAY0005207.csv", "Additional_Inquiry_Details_EN.xlsx"],
            "Additional_Inquiry_Details_EN.xlsx");

        var prompt = new ArtifactPromptComposer().ComposeBilingualManufacturerMailPrompt(
            "Excel Workbook (.xlsx)",
            "Additional_Inquiry_Details_EN.xlsx",
            "- Sheet1!A1: Question 1",
            new ArtifactPromptContext
            {
                ProductName = "Checkmarx",
                SupportId = "00018742",
                CompanyName = "Customer",
                InquiryText = "お客様の追加質問: Question 1",
                UserInstruction = "前回回答を前提に追加確認する",
                CurrentCaseEvidenceReferences = "- Role: CurrentCustomerDelta\n  File: 追加問い合わせ内容.xlsx\n  Excerpt: お客様の追加質問",
                FollowUpScope = scope,
                ManufacturerSafeContext = new ManufacturerSafeContext
                {
                    ProductName = "Checkmarx",
                    SupportId = "00018742",
                    RequestedTask = "前回回答を前提に追加確認する",
                    CurrentCustomerDeltaTechnicalContent = ["お客様の追加質問: Question 1"],
                    CurrentOutboundAttachments = ["Additional_Inquiry_Details_EN.xlsx"],
                },
            },
            scope.CurrentOutboundAttachments);

        Assert.Contains("今回のお客様からの追加確認事項", prompt);
        Assert.Contains("お客様の追加質問: Question 1", prompt);
        Assert.Contains("Additional_Inquiry_Details_EN.xlsx", prompt);
        Assert.DoesNotContain("KSAY0005207.csv", prompt);
        Assert.DoesNotContain("translation target element = 0", prompt);
        Assert.DoesNotContain("以前送付した資料", prompt);
    }

    [Fact]
    public void Resolve_BindsDeltaToAdditionalCurrentCaseFile_NotArbitraryEmailText()
    {
        var scope = ManufacturerFollowUpScopeResolver.Resolve(
            [
                Source("old-mail", "メーカー連携内容_00018742.txt", "追加問い合わせを受けました。クローズしたい。", "PriorManufacturerRequest"),
                Source("initial", "問い合わせ内容.xlsx", "初回質問です。", "CurrentCustomerDelta"),
                Source("additional", "追加問い合わせ内容.xlsx", "質問1: GetSecurePathを確認してください。", "CurrentCustomerDelta"),
            ],
            [
                FileInfo("問い合わせ内容.xlsx", CodexCaseFileKind.CustomerInquiry),
                FileInfo("追加問い合わせ内容.xlsx", CodexCaseFileKind.CustomerInquiry),
                FileInfo("Inquiry_Details_EN.xlsx", CodexCaseFileKind.CustomerInquiry),
            ],
            ["Inquiry_Details_EN.xlsx"],
            "Inquiry_Details_EN.xlsx",
            "00018742");

        var delta = Assert.Single(scope.CurrentCustomerDelta);
        Assert.Equal("追加問い合わせ内容.xlsx", delta.Title);
        Assert.Equal("追加問い合わせ内容.xlsx", scope.CurrentCustomerDeltaSourceFileName);
        Assert.Equal("CustomerInquiry", scope.CurrentCustomerDeltaSourceRole);
        Assert.Equal("00018742", scope.CurrentCustomerDeltaSupportId);
        Assert.DoesNotContain("クローズ", delta.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UsesLatestAppendOnlyCustomerEntryInsteadOfWholeFile()
    {
        var customerHistory = """
            *****追記部_2026/09/10 09:00:00(対応済み)******
            T-SQL parserの過去質問です。USE_NEW_SQLを確認しました。
            *****追記部_2026/09/12 10:00:00(回答済み)******
            Razorの.razor対応について回答済みです。RazorParsingStartedも確認済みです。
            *****追記部_2026/09/14 15:59:03(クローズ確認中)******
            Project Settings > Rules > SASTで+ Add RuleをクリックするとFindings Analysisが追加されます。
            このRuleはグレーアウトされ、defaultConfigを選択できません。Service Userと権限の関係を確認してください。
            """;
        var manufacturerHistory = """
            *****追記部_2026/09/10 11:00:00(送信済み)******
            T-SQL parserについてメーカーへ確認しました。
            *****追記部_2026/09/14 16:10:00(送信済み)******
            メーカーへの確認依頼: defaultConfigがRule候補に表示されない原因とService User / permissionの関係を確認してください。
            """;
        var replyHistory = """
            *****追記部_2026/09/14 16:20:00(回答済み)******
            お客様へ前回のメーカー回答を案内しました。
            """;

        SearchSource[] evidence =
        [
            Source("customer-history", "お客様ご相談内容_00018303.txt", customerHistory, ""),
            Source("manufacturer-history", "メーカー連携内容_00018303.txt", manufacturerHistory, ""),
            Source("reply-history", "お客様への返信案_00018303.txt", replyHistory, ""),
        ];
        CodexCaseFileInfo[] files =
        [
            FileInfo("お客様ご相談内容_00018303.txt", CodexCaseFileKind.CustomerInquiry),
            FileInfo("メーカー連携内容_00018303.txt", CodexCaseFileKind.Other),
            FileInfo("お客様への返信案_00018303.txt", CodexCaseFileKind.Other),
        ];

        var scope = ManufacturerFollowUpScopeResolver.Resolve(
            evidence,
            files,
            ["CxOne_Default_Config_Project_Settings_Guide_EN.docx"],
            "CxOne_Default_Config_Project_Settings_Guide_EN.docx",
            "00018303");

        var delta = Assert.Single(scope.CurrentCustomerDelta);
        Assert.Equal("CUSTOMER_INQUIRY", delta.EvidenceKind);
        Assert.Equal("2026-09-14T15:59:03", delta.RetrievedAt?.ToString("yyyy-MM-dd'T'HH:mm:ss"));
        Assert.Contains("Findings Analysis", delta.Text, StringComparison.Ordinal);
        Assert.Contains("defaultConfig", delta.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("T-SQL", delta.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("RazorParsingStarted", delta.Text, StringComparison.Ordinal);
        Assert.Equal(ManufacturerDraftMode.FollowUp, scope.Mode);
        Assert.True(scope.PreviousManufacturerContactConfirmed);
        Assert.True(scope.PreviousCustomerReplyFound);
        Assert.Contains(scope.PriorManufacturerRequest, item => item.Text.Contains("defaultConfig", StringComparison.Ordinal));
    }

    private static SearchSource Source(
        string id,
        string title,
        string text,
        string role) => new()
        {
            SourceId = id,
            SourceType = "CurrentCase",
            Title = title,
            Text = text,
            SourceRole = role,
            Locator = "line:1",
        };

    private static CodexCaseFileInfo FileInfo(string name, CodexCaseFileKind kind) => new()
    {
        FileName = name,
        RelativePath = name,
        Kind = kind,
        LastModifiedAt = DateTimeOffset.UtcNow,
    };
}
