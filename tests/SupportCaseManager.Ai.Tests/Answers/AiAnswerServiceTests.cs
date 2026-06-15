using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Answers;

public class AiAnswerServiceTests
{
    [Fact]
    public async Task GenerateDraftAsync_CreatesAnswerDraftResultFromMockLlmJson()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "Please check settings.",
              "internalMemo": "source-1 referenced.",
              "needConfirmations": [
                { "question": "Confirm target version.", "reason": "Evidence has conditions.", "priority": "High", "relatedSourceIds": ["source-1"] }
              ],
              "evidence": [
                { "sourceId": "source-1", "sourceType": "PastCase", "title": "Similar case", "excerpt": "Evidence text", "relevance": 0.8 }
              ],
              "confidence": 0.7,
              "warnings": [],
              "generatedAt": "2026-06-02T10:31:00+09:00"
            }
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.Equal("Please check settings.", result.CustomerReplyDraft);
        Assert.Equal("source-1 referenced.", result.InternalMemo);
        Assert.Single(result.NeedConfirmations);
        Assert.Single(result.Evidence);
        Assert.Equal(0.7, result.Confidence);
    }

    [Fact]
    public async Task GenerateDraftAsync_RemovesInternalPathFromCustomerReply()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "C:\\Support\\Cases\\SUP-001\\note.txt を確認してください。",
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.7,
              "warnings": [],
              "generatedAt": "2026-06-02T10:31:00+09:00"
            }
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.DoesNotContain(@"C:\Support\Cases", result.CustomerReplyDraft);
        Assert.Contains("[内部パス削除]", result.CustomerReplyDraft);
        Assert.Contains(result.Warnings, warning => warning.Contains("内部パス", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDraftAsync_ReturnsWarningWhenLlmJsonParsingFails()
    {
        var service = CreateService(@"JSONではない応答です。C:\Support\Cases\SUP-001\note.txt");

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.Contains("JSONではない応答です。", result.CustomerReplyDraft);
        Assert.DoesNotContain(@"C:\Support\Cases", result.CustomerReplyDraft);
        Assert.Contains(result.Warnings, warning => warning.Contains("JSON解析に失敗", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDraftAsync_UsesLowConfidenceWhenNoEvidence()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "根拠が不足しています。",
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0,
              "warnings": [],
              "generatedAt": "2026-06-02T10:31:00+09:00"
            }
            """);

        var result = await service.GenerateDraftAsync(CreateRequest([]));

        Assert.Equal(0.0, result.Confidence);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task GenerateDraftAsync_DoesNotModifyExistingFiles()
    {
        using var temp = new TempDirectory();
        var filePath = System.IO.Path.Combine(temp.Path, "note.txt");
        await File.WriteAllTextAsync(filePath, "既存ノート");
        var expectedLastWriteTime = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(filePath, expectedLastWriteTime);
        var service = CreateService("""
            {
              "customerReplyDraft": "回答案です。",
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.5,
              "warnings": [],
              "generatedAt": "2026-06-02T10:31:00+09:00"
            }
            """);
        var request = CreateRequest(
            [
                new SearchSource
                {
                    SourceId = "source-1",
                    SourceType = "PastCase",
                    Title = "一時ノート",
                    Text = "根拠本文",
                    FilePath = filePath,
                    Score = 0.8,
                },
            ]);

        _ = await service.GenerateDraftAsync(request);

        Assert.Equal(expectedLastWriteTime, File.GetLastWriteTime(filePath));
        Assert.Equal("既存ノート", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task GenerateDraftAsync_UsesResolvedFactsForAutoAnswerableLatestVersionQuestion()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "string",
              "internalMemo": "string",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.4,
              "warnings": []
            }
            """);
        var request = CreateRequest([]) with
        {
            InquiryText = "現在のCxSAST最新バージョンは何でしょうか？EP、HFの最新バージョンも教えてください。",
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = AnswerReadiness.AutoAnswerable,
                LlmPromptUsesResolvedFacts = true,
                Classification = new QuestionClassificationResult
                {
                    QuestionTypes = [QuestionTypes.LatestVersionQuestion],
                    RequestedFacts =
                    [
                        FactKeys.LatestSastVersion,
                        FactKeys.LatestEnginePackVersion,
                        FactKeys.LatestHotfixVersion,
                    ],
                },
                ResolvedFacts =
                [
                    CreateResolvedFact(FactKeys.LatestSastVersion, "9.7.0"),
                    CreateResolvedFact(FactKeys.LatestEnginePackVersion, "9.7.6"),
                    CreateResolvedFact(FactKeys.LatestHotfixVersion, "HF10"),
                ],
            },
        };

        var result = await service.GenerateDraftAsync(request);

        Assert.Contains("9.7.0", result.CustomerReplyDraft);
        Assert.Contains("9.7.6", result.CustomerReplyDraft);
        Assert.Contains("HF10", result.CustomerReplyDraft);
        Assert.DoesNotContain("アップグレード可能", result.CustomerReplyDraft);
        Assert.True(result.Confidence >= 0.9);
        Assert.Contains("ResolvedFacts", result.InternalMemo);
        Assert.Contains("Curated", result.InternalMemo);
    }

    private static AiAnswerService CreateService(string llmResponse)
    {
        return new AiAnswerService(
            new PromptBuilder(),
            new EvidenceBuilder(),
            new SafetyRedactionService(),
            new FakeLlmClient(llmResponse));
    }

    private static AnswerDraftRequest CreateRequest(IReadOnlyList<SearchSource>? sources = null)
    {
        return new AnswerDraftRequest
        {
            Case = new CaseContext
            {
                CompanyName = "株式会社サンプル",
                SupportNumber = "SUP-001",
            },
            InquiryText = "問い合わせ本文",
            Sources = sources ??
            [
                new SearchSource
                {
                    SourceId = "source-1",
                    SourceType = "PastCase",
                    Title = "類似案件",
                    Text = "根拠本文",
                    Score = 0.8,
                },
            ],
            Settings = new AiAssistantSettings(),
        };
    }

    private static ResolvedFact CreateResolvedFact(string key, string value)
    {
        return new ResolvedFact
        {
            Key = key,
            Value = value,
            Status = FactStatuses.Confirmed,
            Confidence = FactConfidences.High,
            SourceType = "Curated",
            SourceUrls = [$"https://docs.example.test/{key}"],
            Explanation = "test",
        };
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly string response;

        public FakeLlmClient(string response)
        {
            this.response = response;
        }

        public Task<LlmGenerationResult> GenerateAsync(
            PromptMessages messages,
            LlmProviderSettings settings,
            bool disableThinking = true,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmGenerationResult { Content = response, DoneReason = "stop" });
        }
    }
}
