using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class AuditedFactGenerationTests
{
    [Fact]
    public void InitialAndRepairUseSameAttributedFactAndRequestConditions()
    {
        const string inquiry = "Windows 11 Pro 24H2でツールの入手先とセットアップ手順を教えてください。";
        var resolution = ImportantFactContract.Enrich(new FactResolutionResult
        {
            AnswerReadiness = "InsufficientEvidence",
            ResolvedFacts = [new ResolvedFact
            {
                Key = "AuditedCaseFact", Value = "添付BATにはDashboardサービスを停止する操作が記録されている。",
                SourceType = "CurrentCase", Status = "Candidate", EvidenceId = "case:hash",
                Explanation = "AuditedAttachment",
            }],
        }, inquiry, null, new SafetyRedactionService());
        var request = new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = "Synthetic" }, InquiryText = inquiry,
            FactResolution = resolution, Settings = new AiAssistantSettings { MaxPromptChars = 8000 },
        };
        foreach (var feedback in new string?[] { null, "重要Factが欠落しています。" })
        {
            var prompt = GroundedAnswerPromptBuilder.Build(request, [], feedback);
            Assert.Contains("Windows 11 Pro 24H2", prompt.UserPrompt);
            Assert.Contains("監査済み添付", prompt.UserPrompt);
            Assert.Contains("Dashboardサービスを停止", prompt.UserPrompt);
            Assert.Contains("製品仕様の証明ではありません", prompt.UserPrompt);
            var pattern = prompt.OutputSchema!.Value.GetProperty("properties").GetProperty("customerReplyDraft").GetProperty("pattern").GetString()!;
            Assert.DoesNotContain("Dashboard", pattern);
            Assert.DoesNotContain("24H2", pattern);
            Assert.Contains("UNTRUSTED_FACT_RESOLUTION_JSON", prompt.UserPrompt);
            Assert.True(prompt.Diagnostics.FinalPromptChars <= 8000);
        }
        Assert.NotNull(ImportantFactContract.Validate(resolution, "確認できる事実: 設定済み。"));
        Assert.NotNull(ImportantFactContract.Validate(resolution, "公式仕様ではDashboardサービスを停止します。"));
    }

    [Fact]
    public void PreservesRemovalAndPlannedOperationInsteadOfOnlyVersion()
    {
        const string inquiry = "CxSAST 9.7.4 HF4で改善済みならカスタムクエリを削除したいです。";
        var resolution = ImportantFactContract.Enrich(new FactResolutionResult { AnswerReadiness = "NeedsReview" },
            inquiry, null, new SafetyRedactionService());
        Assert.Contains(ImportantFactContract.Select(resolution), f => f.Key == "ImportantInquiryIntent" && f.Operation == "依頼軸・条件");
        Assert.NotNull(ImportantFactContract.Validate(resolution, "CxSAST 9.7.4 HF4について確認してください。"));
        var prompt = GroundedAnswerPromptBuilder.Build(new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = "Synthetic" }, InquiryText = inquiry,
            FactResolution = resolution, Settings = new AiAssistantSettings { MaxPromptChars = 8000 },
        }, []);
        Assert.Contains("カスタムクエリを削除したい", prompt.UserPrompt);
    }
}
