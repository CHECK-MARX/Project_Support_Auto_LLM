using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Prompts;

namespace SupportCaseManager.Ai.Tests.Prompts;

public sealed class AnswerGroundingTests
{
    [Fact]
    public void Build_InstructsModelToJudgeEvidenceRelevanceFirst()
    {
        var messages = new PromptBuilder().Build(CreateRequest());

        Assert.Contains("直接関係するか", messages.SystemPrompt);
        Assert.Contains("関係が弱い根拠", messages.SystemPrompt);
        Assert.Contains("各参照根拠について、問い合わせと直接関係するかを評価", messages.UserPrompt);
    }

    [Fact]
    public void Build_InstructsModelNotToInventUnsupportedFacts()
    {
        var messages = new PromptBuilder().Build(CreateRequest());

        Assert.Contains("存在しない」と断定しない", messages.SystemPrompt);
        Assert.Contains("確認できません", messages.UserPrompt);
    }

    [Fact]
    public void Build_IncludesInsufficiencyInstructionsForInternalMemo()
    {
        var messages = new PromptBuilder().Build(CreateRequest());

        Assert.Contains("使用した根拠ID", messages.SystemPrompt);
        Assert.Contains("不足している確認事項", messages.UserPrompt);
    }

    [Fact]
    public void Build_IncludesScoreMetadataForSelectedEvidence()
    {
        var messages = new PromptBuilder().Build(CreateRequest());

        Assert.Contains("matchedTerms: ライセンス, 認証", messages.UserPrompt);
        Assert.Contains("queryCoverage: 2/3", messages.UserPrompt);
        Assert.Contains("scoreBreakdown: coverage=0.67", messages.UserPrompt);
    }

    private static AnswerDraftRequest CreateRequest()
    {
        return new AnswerDraftRequest
        {
            Case = new CaseContext
            {
                ProductName = "製品A",
                SupportNumber = "SUP-001",
            },
            InquiryText = "ライセンス認証エラーについて確認したいです。",
            Sources =
            [
                new SearchSource
                {
                    SourceId = "manual-license",
                    SourceType = "Manual",
                    Title = "ライセンス認証エラー対応手順",
                    Text = "ライセンスサーバー名とポート番号を確認します。",
                    Score = 0.82,
                    MatchedTerms = ["ライセンス", "認証"],
                    QueryCoverage = "2/3",
                    ScoreBreakdown = "coverage=0.67; fieldStrength=0.80; title=1; body=2; metadata=0",
                },
            ],
            Settings = new AiAssistantSettings(),
        };
    }
}
