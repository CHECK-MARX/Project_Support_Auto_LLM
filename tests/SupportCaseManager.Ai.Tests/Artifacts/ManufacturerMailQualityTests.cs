using SupportCaseManager.Ai.Core.Artifacts;

namespace SupportCaseManager.Ai.Tests.Artifacts;

public sealed class ManufacturerMailQualityTests
{
    [Fact]
    public void QualityValidator_RequiresFormalFollowUpStructureWithoutReplacingBoundaryChecks()
    {
        var safe = CreateSafeContext();
        var pair = new ManufacturerDraftPair
        {
            JapaneseDraft = """
                件名：Path Traversal検出結果に関する追加確認（Support ID: 00018742）
                メーカーサポートご担当者様
                お世話になっております。
                東陽テクニカの伊藤です。
                先日はご回答をいただき、ありがとうございました。お客様へ回答内容をご案内したところ、追加確認を受領しました。
                前回のご回答では、GetSecurePathをSanitizerとして扱うことは推奨されないとのことでした。
                お客様は現在の実装方針をできれば維持したいと希望しています。質問1：GetSecurePathの検証方法をご教示ください。
                今回はAdditional_Inquiry_Details_EN.xlsxを添付いたします。
                追加資料が必要な場合はお知らせください。
                どうぞよろしくお願いいたします。
                株式会社東陽テクニカ
                伊藤 健
                """,
            EnglishDraft = """
                Subject: Additional questions about the Path Traversal findings (Support ID: 00018742)
                Hello Support Team,
                This is Ken Ito from Toyo Corporation.
                Thank you for your previous response. We shared it with our customer and received additional questions.
                Your previous response stated that GetSecurePath is not recommended as a Path Traversal sanitizer.
                Our customer would like to maintain the current implementation where possible. Question 1: Could you explain the recommended validation for GetSecurePath?
                We have attached Additional_Inquiry_Details_EN.xlsx.
                Please let us know if additional materials are required.
                Best regards,
                Ken Ito
                Toyo Corporation
                """,
        };

        var result = new ManufacturerMailQualityValidator().Validate(pair, safe);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Issues));
    }

    [Fact]
    public void QualityValidator_ReportsIncompleteMailSeparatelyFromDataBoundary()
    {
        var safe = CreateSafeContext();
        var pair = new ManufacturerDraftPair
        {
            JapaneseDraft = "件名：確認\nファイル内容は確認できません。",
            EnglishDraft = "Subject: Review\nCurrentCase Evidence is missing.",
        };

        var result = new ManufacturerMailQualityValidator().Validate(pair, safe);

        Assert.False(result.Succeeded);
        Assert.Contains("Japanese.Recipient", result.MissingSections);
        Assert.Contains("English.PriorResponseThanks", result.MissingSections);
        Assert.Contains("Forbidden:CurrentCase Evidence", result.ForbiddenContent);
        Assert.Contains("Forbidden:ファイル内容は確認できません", result.ForbiddenContent);
    }

    [Fact]
    public void MailComposer_ProvidesFormalContractAndPriorTechnicalContext()
    {
        var prompt = new ManufacturerMailComposer().ComposeBilingualPrompt(
            "Excel Workbook (.xlsx)",
            "Additional_Inquiry_Details_EN.xlsx",
            "追加質問の英訳",
            CreateSafeContext());

        Assert.Contains("FORMAL MANUFACTURER MAIL WRITING CONTRACT", prompt);
        Assert.Contains("ManufacturerMailBrief", prompt);
        Assert.DoesNotContain("前回の技術回答では、item", prompt);
        Assert.Contains("GetSecurePath", prompt);
        Assert.Contains("Additional_Inquiry_Details_EN.xlsx", prompt);
        Assert.Contains("質問理由を含む", prompt);
        Assert.Contains("同じ情報密度", prompt);
        Assert.Contains("自動送信・ファイル追記・案件ファイル変更は行いません", prompt);
        Assert.DoesNotContain("KSAY0005207.zip", prompt);
    }

    private static ManufacturerSafeContext CreateSafeContext() => new()
    {
        DraftMode = ManufacturerDraftMode.FollowUp,
        ProductName = "Checkmarx",
        ProductVersion = "9.7.4.1008 HF9",
        SupportId = "00018742",
        CurrentCustomerDeltaTechnicalContent = ["お客様は現在の実装方針を維持したいと希望しています。", "質問1：GetSecurePathの検証方法をご教示ください。"],
        RelevantPriorManufacturerResponse = ["前回のご回答では、GetSecurePathをSanitizerとして扱うことは推奨されないとのことでした。"],
        PriorTechnicalContext = "前回の技術回答では、itemとrevを結合してからPath.GetFileName()を使用する案が示されました。",
        CurrentOutboundAttachments = ["Additional_Inquiry_Details_EN.xlsx"],
        RequiredProtectedTechnicalValues = ["Checkmarx", "00018742", "GetSecurePath", "Path.GetFileName()", "Additional_Inquiry_Details_EN.xlsx"],
        ManufacturerRecipient = new ManufacturerRecipient { ResolutionStatus = "UNRESOLVED" },
    };
}
