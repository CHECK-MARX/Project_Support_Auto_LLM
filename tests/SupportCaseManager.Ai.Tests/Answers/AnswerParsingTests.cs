using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Tests.Answers;

public class AnswerParsingTests
{
    [Fact]
    public async Task GenerateDraftAsync_UsesCustomerReplyDraftFromPlainJson()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "ライセンスサーバー名とポート番号を確認してください。",
              "internalMemo": "source-1を参照。",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.75,
              "warnings": []
            }
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.Equal("ライセンスサーバー名とポート番号を確認してください。", result.CustomerReplyDraft);
        Assert.DoesNotContain("customerReplyDraft", result.CustomerReplyDraft);
    }

    [Fact]
    public async Task GenerateDraftAsync_ParsesJsonFromFencedCodeBlockAndDoesNotShowFullJson()
    {
        var service = CreateService("""
            ```json
            {
              "customerReplyDraft": "ファイアウォール設定を確認してください。",
              "internalMemo": "manual-1を参照。",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.6,
              "warnings": []
            }
            ```
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.Equal("ファイアウォール設定を確認してください。", result.CustomerReplyDraft);
        Assert.DoesNotContain("{", result.CustomerReplyDraft);
    }

    [Fact]
    public async Task GenerateDraftAsync_ParsesJsonWithPrefixAndSuffixText()
    {
        var service = CreateService("""
            以下です。
            {
              "customerReplyDraft": "認証エラーの状況を確認してください。",
              "internalMemo": "memo",
              "needConfirmations": [],
              "evidence": [],
              "confidence": "0.75",
              "warnings": []
            }
            以上です。
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.Equal("認証エラーの状況を確認してください。", result.CustomerReplyDraft);
        Assert.Equal(0.75, result.Confidence);
    }

    [Fact]
    public async Task GenerateDraftAsync_ConvertsObjectInternalMemoToReadableText()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "回答案です。",
              "internalMemo": {
                "sourceId": "source-1",
                "sourceType": "PastCaseNote",
                "title": "類似案件"
              },
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.5,
              "warnings": []
            }
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.Contains("sourceId: source-1", result.InternalMemo);
        Assert.Contains("title: 類似案件", result.InternalMemo);
        Assert.DoesNotContain("{", result.InternalMemo);
    }

    [Fact]
    public async Task GenerateDraftAsync_ParsesNeedConfirmationsAsList()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "回答案です。",
              "internalMemo": "",
              "needConfirmations": [
                { "question": "製品バージョンを確認してください。", "reason": "本文に記載がありません。", "priority": "High" }
              ],
              "evidence": [],
              "confidence": 0.5,
              "warnings": []
            }
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        var item = Assert.Single(result.NeedConfirmations);
        Assert.Equal("製品バージョンを確認してください。", item.Question);
        Assert.Equal("High", item.Priority);
    }

    [Fact]
    public async Task GenerateDraftAsync_KeepsOnlyEvidenceWithProvidedSourceIds()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "回答案です。",
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [
                { "sourceId": "source-1", "sourceType": "PastCaseNote", "title": "採用する根拠", "excerpt": "抜粋", "relevance": 0.9 },
                { "sourceId": "not-provided", "sourceType": "Manual", "title": "除外する根拠", "excerpt": "抜粋", "relevance": 0.8 }
              ],
              "confidence": 0.7,
              "warnings": []
            }
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        var evidence = Assert.Single(result.Evidence);
        Assert.Equal("source-1", evidence.SourceId);
        Assert.Contains(result.Warnings, warning => warning.Contains("not-provided", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDraftAsync_ParsesStringWarningsAndInvalidJsonSafely()
    {
        var service = CreateService("""
            { "customerReplyDraft": "抽出可能な回答案", "internalMemo":
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.Equal("抽出可能な回答案", result.CustomerReplyDraft);
        Assert.Contains(result.Warnings, warning => warning.Contains("JSON解析に失敗", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDraftAsync_InvalidJsonWithoutDraftDoesNotExposeRawResponse()
    {
        var rawResponse = "これはJSONではありません。{ invalid json";
        var service = CreateService(rawResponse);

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.Equal("LLM応答を解析できませんでした。回答内容を確認してください。", result.CustomerReplyDraft);
        Assert.DoesNotContain(rawResponse, result.CustomerReplyDraft);
        Assert.Contains(result.Warnings, warning => warning.Contains("JSON解析に失敗", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDraftAsync_UsesNaturalLanguageFallbackWhenResponseIsNotJson()
    {
        var service = CreateService("""
            CxSASTの対応言語は、対象バージョンのマニュアルに記載されている対応言語一覧で確認してください。
            送信いただいた根拠では、C/C++、C#、Javaなどの言語に関する記載が確認できます。
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.Contains("CxSASTの対応言語", result.CustomerReplyDraft);
        Assert.Contains("C/C++", result.CustomerReplyDraft);
        Assert.Contains(result.Warnings, warning => warning.Contains("JSON解析に失敗", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDraftAsync_RemovesThinkingBlockBeforeNaturalLanguageFallback()
    {
        var service = CreateService("""
            <think>
            内部推論です。これはユーザーに表示しません。
            </think>
            CxSASTの対応言語は、参照マニュアルの対応言語一覧を確認してください。
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.Contains("CxSASTの対応言語", result.CustomerReplyDraft);
        Assert.DoesNotContain("内部推論", result.CustomerReplyDraft);
        Assert.DoesNotContain("<think>", result.CustomerReplyDraft);
    }

    private static AiAnswerService CreateService(string llmResponse)
    {
        return new AiAnswerService(
            new PromptBuilder(),
            new EvidenceBuilder(),
            new SafetyRedactionService(),
            new FakeLlmClient(llmResponse));
    }

    private static AnswerDraftRequest CreateRequest()
    {
        return new AnswerDraftRequest
        {
            Case = new CaseContext
            {
                ProductName = "HelixQAC",
                CompanyName = "株式会社サンプル",
                SupportNumber = "00017581",
            },
            InquiryText = "ライセンス認証エラーで製品が起動できません。",
            Sources =
            [
                new SearchSource
                {
                    SourceId = "source-1",
                    SourceType = "PastCaseNote",
                    Title = "類似案件",
                    Text = "ライセンスサーバー名とポート番号を確認します。",
                    FilePath = @"D:\Cases\00017581\note.txt",
                    SupportNumber = "00017581",
                    Score = 0.9,
                },
            ],
            Settings = new AiAssistantSettings { MaxEvidenceItems = 8 },
        };
    }

    private sealed class FakeLlmClient(string response) : ILlmClient
    {
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
