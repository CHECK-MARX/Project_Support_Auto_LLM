using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class ImportantInquiryAxesTests
{
    private static FactResolutionResult Resolve(string inquiry) => ImportantFactContract.Enrich(
        new FactResolutionResult { AnswerReadiness = AnswerReadiness.NeedsManufacturerConfirmation },
        inquiry, null, new SafetyRedactionService());

    [Fact]
    public void Options_MustRemainSeparateAndDoNotBecomeProductSpecifications()
    {
        var facts = Resolve("以下のサービスのうち使用可能なものを教えてください。\n1．Azure SQL Database\n2．Azure SQL Managed Instance\n3．SQL Server on Windows VM");
        Assert.Equal(3, facts.ResolvedFacts.Count);
        Assert.All(facts.ResolvedFacts, fact =>
        {
            Assert.Equal("CurrentInquiry", fact.SourceType);
            Assert.Equal("Candidate", fact.Status);
            Assert.Contains("顧客", ImportantFactContract.DescribeForGeneration(fact));
        });
        Assert.NotNull(ImportantFactContract.Validate(facts, "Azure SQL Databaseは未確認です。"));
        Assert.Null(ImportantFactContract.Validate(facts,
            "Azure SQL Database、Azure SQL Managed Instance、SQL Server on Windows VMの可否を質問されています。対象版を確認します。"));
    }

    [Fact]
    public void NumberedAttachmentSections_AreNotAnswerOptions()
    {
        Assert.Empty(Resolve("以下の理解で合っていますか。\n1.ログデータ\n2.御社HP").ResolvedFacts);
    }

    [Fact]
    public void FalsePositiveIntent_IsAQuestionAndCannotBeOmitted()
    {
        var facts = Resolve("診断の指摘は誤検知ではないかと思っております。");
        Assert.Single(facts.ResolvedFacts);
        Assert.NotNull(ImportantFactContract.Validate(facts, "診断が検出されました。メーカー原文を確認します。"));
        Assert.Null(ImportantFactContract.Validate(facts, "誤検知の可能性をご相談いただいています。適用条件はメーカーへ確認します。"));
    }

    [Fact]
    public void Migration_KeepsVersionRolesAndEnabledQuestionInSameGenerationContract()
    {
        const string inquiry = "v2025.2からv2026.2へのバージョンアップを計画しています。\nデフォルトのenabledフィールドに変更があるか確認したいです。";
        var facts = Resolve(inquiry);
        var prompt = GroundedAnswerPromptBuilder.Build(new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = "Synthetic" }, InquiryText = inquiry,
            FactResolution = facts, Sources = [], Settings = new AiAssistantSettings { MaxPromptChars = 12000 },
        }, []);
        Assert.Contains("operation=版の移行計画", prompt.UserPrompt);
        Assert.Contains("enabled", prompt.UserPrompt);
        Assert.Contains("顧客の質問軸", prompt.UserPrompt);
        Assert.NotNull(ImportantFactContract.Validate(facts, "2025.2と2026.2の資料を比較します。"));
        Assert.Null(ImportantFactContract.Validate(facts,
            "2025.2から2026.2へのバージョンアップ計画におけるenabledフィールドの変更有無はメーカーへ確認します。"));
        Assert.NotNull(ImportantFactContract.Validate(facts,
            "2026.2から2025.2へのバージョンアップにおけるenabled変更を確認します。"));
    }
}
