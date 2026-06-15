using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class FreshnessAnswerGuardTests
{
    [Fact]
    public async Task GenerateDraftAsync_DoesNotAssertLatestVersionFromPastCasesOnly()
    {
        var request = new AnswerDraftRequest
        {
            InquiryText = "最新バージョンとEP/HFを確認したいです。",
            InquiryFocus = new InquiryFocus
            {
                FocusText = "最新バージョンとEP/HFを確認したいです。",
                IsFreshnessSensitive = true,
                FreshnessReason = "最新 / EP / HF を含むため",
            },
            Sources =
            [
                new SearchSource
                {
                    SourceId = "case-1",
                    SourceType = "PastCaseNote",
                    Title = "過去案件",
                    Text = "過去案件では2024.1 HF1が最新です。",
                    Score = 0.9,
                },
            ],
            Settings = new AiAssistantSettings(),
        };
        var service = CreateService("""
            {
              "customerReplyDraft": "最新版は2024.1 HF1です。",
              "internalMemo": "case-1",
              "evidence": [{"sourceId": "case-1", "sourceType": "PastCaseNote", "title": "過去案件", "excerpt": "2024.1", "relevance": 0.9}],
              "confidence": 0.8,
              "warnings": []
            }
            """);

        var result = await service.GenerateDraftAsync(request);

        Assert.Contains("公式情報", result.CustomerReplyDraft);
        Assert.Contains("断定", result.CustomerReplyDraft);
        Assert.Contains(result.Warnings, warning => warning.Contains("OfficialDocなし", StringComparison.Ordinal));
        Assert.True(result.Confidence <= 0.35);
        Assert.Contains("OfficialDoc根拠がない", result.InternalMemo);
    }

    [Fact]
    public async Task GenerateDraftAsync_KeepsOfficialDocFreshnessAnswerButFormatsMemo()
    {
        var request = new AnswerDraftRequest
        {
            InquiryText = "最新バージョンを確認したいです。",
            InquiryFocus = new InquiryFocus
            {
                FocusText = "最新バージョンを確認したいです。",
                IsFreshnessSensitive = true,
                FreshnessReason = "最新バージョンを含むため",
            },
            Sources =
            [
                new SearchSource
                {
                    SourceId = "official-1",
                    SourceType = "OfficialDoc",
                    Title = "Release Notes",
                    Text = "最新バージョンは2026.1です。",
                    Url = "https://docs.example.test/release",
                    Score = 0.95,
                },
            ],
            Settings = new AiAssistantSettings(),
        };
        var service = CreateService("""
            {
              "customerReplyDraft": "お問い合わせいただいた件について、確認結果を以下に記載します。\n\n公式情報では最新バージョンは2026.1です。\n\n必要に応じて適用条件を確認します。",
              "internalMemo": "string",
              "evidence": [{"sourceId": "official-1", "sourceType": "OfficialDoc", "title": "Release Notes", "excerpt": "2026.1", "relevance": 0.95}],
              "confidence": 0.8,
              "warnings": []
            }
            """);

        var result = await service.GenerateDraftAsync(request);

        Assert.Contains("2026.1", result.CustomerReplyDraft);
        Assert.Contains("LLMへ送信した根拠", result.InternalMemo);
        Assert.True(result.Confidence > 0.35);
    }

    private static AiAnswerService CreateService(string llmResponse)
    {
        return new AiAnswerService(
            new PromptBuilder(),
            new EvidenceBuilder(),
            new SafetyRedactionService(),
            new StaticLlmClient(llmResponse));
    }

    private sealed class StaticLlmClient : ILlmClient
    {
        private readonly string response;

        public StaticLlmClient(string response)
        {
            this.response = response;
        }

        public Task<LlmGenerationResult> GenerateAsync(PromptMessages messages, LlmProviderSettings settings, bool disableThinking = true, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmGenerationResult { Content = response, DoneReason = "stop" });
        }
    }
}
