using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Safety;

namespace SupportCaseManager.Ai.Tests.Answers;

public sealed class GroundedAnswerComparisonTests
{
    private const string EvidenceText = "設定画面でポート番号1234を指定してください。";

    [Fact]
    public async Task CompareAsync_UsesBaselineEvidenceAndKeepsBaselineSeparate()
    {
        var client = new RecordingLlmClient(Response(EvidenceText, EvidenceText));
        var baseline = Baseline();

        var comparison = await new GroundedAnswerComparisonService(
            client, new SafetyRedactionService(), new FixedChecker()).CompareAsync(Request(), baseline);

        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, comparison.Status);
        Assert.Same(baseline, comparison.Baseline);
        Assert.NotNull(comparison.Candidate);
        Assert.NotNull(comparison.BaselineQuality);
        Assert.NotNull(comparison.CandidateQuality);
        Assert.Single(comparison.CitationTraces);
        Assert.Equal(0, comparison.CitationTraces[0].SpanStart);
        Assert.Equal(EvidenceText.Length, comparison.CitationTraces[0].SpanLength);
        Assert.Equal(AnswerGenerationModes.GroundedCandidate, comparison.Candidate.AnswerGenerationMode);
        Assert.Contains(EvidenceText, comparison.Candidate.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("sourceId=s1", client.LastPrompt?.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("unused source", client.LastPrompt?.UserPrompt, StringComparison.Ordinal);
        var body = OllamaRequestBuilder.BuildChatRequestBody(
            Request().Settings.LlmProvider, "system", "user", true, client.LastPrompt?.OutputSchema);
        using var requestJson = JsonDocument.Parse(JsonSerializer.Serialize(body));
        var format = requestJson.RootElement.GetProperty("format");
        Assert.Equal("object", format.GetProperty("type").GetString());
        Assert.Contains(format.GetProperty("required").EnumerateArray(),
            item => item.GetString() == "evidence");
    }

    [Fact]
    public async Task CompareAsync_RejectsQuoteNotFoundInSource()
    {
        var client = new RecordingLlmClient(Response(EvidenceText, "架空の引用"));

        var comparison = await new GroundedAnswerComparisonService(
            client, new SafetyRedactionService(), new FixedChecker()).CompareAsync(Request(), Baseline());

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Null(comparison.Candidate);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("引用", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_AllowsSpecificSafeAbstentionWithoutUnrelatedCitation()
    {
        var request = Request() with
        {
            InquiryText = "VPN経由のライセンスサーバーで利用できますか。",
            FactResolution = new FactResolutionResult { AnswerReadiness = "InsufficientEvidence" },
        };
        var reply = "確認できる事実: VPN経由のライセンスサーバーを検討されています。" +
            "現時点で断定できない事項: 提示資料では利用可否を確認できません。" +
            "追加で必要な確認: 対象版と通信条件を確認し、メーカー見解を取得します。";
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = reply,
            internalMemo = "直接該当する根拠なし",
            needConfirmations = new[] { new { question = "対象版と通信条件を確認してください", reason = "根拠不足", priority = "High" } },
            evidence = Array.Empty<object>(),
            confidence = 0.2,
            warnings = Array.Empty<string>(),
        });

        var comparison = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline());

        Assert.True(comparison.Status == GroundedComparisonStatuses.ReadyForHumanReview,
            string.Join(" | ", comparison.Reasons));
        Assert.Equal("InsufficientEvidence", comparison.Candidate?.Readiness);
        Assert.Empty(comparison.CitationTraces);
        Assert.Equal(0, comparison.GeneratedCitationCount);
    }

    [Fact]
    public async Task CompareAsync_RejectsUnsupportedPositiveClaimWithoutCitation()
    {
        var request = Request() with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "InsufficientEvidence" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "設定画面でポート番号1234を指定すれば解決できます。",
            internalMemo = "",
            needConfirmations = new[] { "設定を確認" },
            evidence = Array.Empty<object>(),
            confidence = 0.2,
            warnings = Array.Empty<string>(),
        });

        var comparison = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline());

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("引用なし", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_CompletesModelProvidedConfirmationIntoSafeReply()
    {
        var request = Request() with
        {
            InquiryText = "VPN経由の社外サーバーをライセンスサーバーの移行先として検討しています。",
            FactResolution = new FactResolutionResult { AnswerReadiness = "InsufficientEvidence" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "ライセンスサーバーの移行先としてVPN経由の社外サーバーを検討されています。ただし、VPN経由での運用可否は現時点では情報がありません。",
            internalMemo = "メーカー確認が必要です。",
            needConfirmations = new[] { new { question = "対象バージョンを明示してください。", reason = "対応条件を確認するため", priority = "High" } },
            evidence = Array.Empty<object>(),
            confidence = 0.2,
            warnings = Array.Empty<string>(),
        });

        var comparison = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline(), shadowReview: new ShadowReviewCriteria(
                ["VPN経由の社外サーバーへ移行を検討している"], ["正式サポートと断定しない"],
                ["メーカー見解が必要"], "InsufficientEvidence"));

        Assert.True(comparison.Status == GroundedComparisonStatuses.ReadyForHumanReview,
            string.Join(" | ", comparison.Reasons));
        Assert.Equal("InsufficientEvidence", comparison.Candidate?.Readiness);
        Assert.Contains("対象バージョンを明示してください", comparison.Candidate?.CustomerReplyDraft);
        Assert.Equal(0, comparison.GeneratedCitationCount);
    }

    [Fact]
    public async Task CompareAsync_NonReadyReplyWithoutCustomerActionIsRejected()
    {
        var request = Request() with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var comparison = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(Response("ポート番号1234が関係する可能性があります。", EvidenceText)),
            new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline(), shadowReview: new ShadowReviewCriteria(
                ["ポート番号1234が関係する"], ["正式サポートと断定しない"],
                ["設定手順"], "NeedsReview"));

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("具体的な追加確認", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_ShadowReadinessPreventsPromotionEvenWhenBaselineIsReady()
    {
        var client = new RecordingLlmClient(Response(EvidenceText, EvidenceText));
        var request = Request() with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var comparison = await new GroundedAnswerComparisonService(
            client,
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(
                request, Baseline(), shadowReview: new ShadowReviewCriteria(
                    ["ポート番号1234を設定する"], ["根拠なく正式サポートと断定"],
                    ["設定手順"], "NeedsReview"));

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Equal("CustomerReady", comparison.GeneratedReadiness);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("Readiness", StringComparison.Ordinal));
        Assert.DoesNotContain("根拠なく正式サポートと断定", client.LastPrompt?.UserPrompt);
        Assert.DoesNotContain("ポート番号1234を設定する", client.LastPrompt?.UserPrompt);
    }

    [Fact]
    public async Task CompareAsync_ShadowParaphraseCannotPassHumanReview()
    {
        var request = Request() with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "CustomerReady" },
        };
        var comparison = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(Response("設定画面で指定するポート番号を教えてください。追加確認します。", EvidenceText)),
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(
                request, Baseline(), shadowReview: new ShadowReviewCriteria(
                    ["CxJobManagerが停止した"], ["原因を断定しない"],
                    ["CxJobManagerのログ"], "CustomerReady"));

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("Expected Claims", StringComparison.Ordinal));
    }

    [Fact]
    public void GroundedPrompt_PreservesServerRelationshipFromInquiry()
    {
        var request = Request() with
        {
            InquiryText = "Validateサーバー を起動できません:データベースサーバー は別のプロジェクトルートで実行中です。",
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };

        var prompt = GroundedAnswerPromptBuilder.Build(request, [request.Sources[0]]);

        Assert.Contains("起動対象=Validateサーバー", prompt.UserPrompt);
        Assert.Contains("衝突対象=データベースサーバー", prompt.UserPrompt);
        Assert.Contains("生成契約Readiness: NeedsReview", prompt.UserPrompt);
        Assert.Contains("対象事象との直接対応: NO", prompt.UserPrompt);
    }

    [Fact]
    public void GroundedPrompt_DistinguishesCustomerPlanFromConfirmedProductCapability()
    {
        var request = Request() with
        {
            InquiryText = "ライセンスサーバーの移行先としてVPN接続している社外リースのサーバーを検討しています。",
            FactResolution = new FactResolutionResult { AnswerReadiness = "InsufficientEvidence" },
        };

        var prompt = GroundedAnswerPromptBuilder.Build(request, [request.Sources[0]]);

        Assert.Contains("顧客報告 sourceId=inquiry: ライセンスサーバーの移行先として", prompt.UserPrompt);
        Assert.Contains("顧客の計画であり、運用可否の証明ではない", prompt.UserPrompt);
        Assert.Contains("Confirmed Fact: 独立した製品仕様の確定Factなし", prompt.UserPrompt);
    }

    [Fact]
    public async Task CompareAsync_RejectsCitationWithWrongSourceType()
    {
        var response = Response(EvidenceText, EvidenceText).Replace(
            "\"sourceId\":\"s1\"", "\"sourceId\":\"s1\",\"sourceType\":\"OfficialDoc\"");
        var comparison = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(Request(), Baseline());

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("引用", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_RejectsIncompleteOrWrappedJson()
    {
        foreach (var response in new[]
        {
            "```json\n" + Response(EvidenceText, EvidenceText) + "\n```",
            "{\"customerReplyDraft\":\"回答\",\"evidence\":[{\"sourceId\":\"s1\"}]}",
        })
        {
            var comparison = await new GroundedAnswerComparisonService(
                new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
                .CompareAsync(Request(), Baseline());

            Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
            Assert.Null(comparison.Candidate);
            Assert.Contains(comparison.Reasons, reason => reason.Contains("JSON形式", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task CompareAsync_RejectsUnprovidedVersion()
    {
        var client = new RecordingLlmClient(Response("バージョン9.9で対応しています。", EvidenceText));

        var comparison = await new GroundedAnswerComparisonService(
            client, new SafetyRedactionService(), new FixedChecker()).CompareAsync(Request(), Baseline());

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Null(comparison.Candidate);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("技術値", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_OverBudgetDoesNotCallModelOrTruncateEvidence()
    {
        var client = new RecordingLlmClient(Response(EvidenceText, EvidenceText));
        var request = Request() with { Settings = new AiAssistantSettings { MaxPromptChars = 100 } };

        var comparison = await new GroundedAnswerComparisonService(
            client, new SafetyRedactionService(), new FixedChecker()).CompareAsync(request, Baseline());

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Equal(0, client.CallCount);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("切り捨てず", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_NonlocalProviderDoesNotCallModel()
    {
        var client = new RecordingLlmClient(Response(EvidenceText, EvidenceText));
        var request = Request() with
        {
            Settings = new AiAssistantSettings
            {
                LlmProvider = new LlmProviderSettings
                {
                    Provider = "Ollama",
                    Endpoint = "https://remote.example.test",
                },
            },
        };

        var comparison = await new GroundedAnswerComparisonService(
            client, new SafetyRedactionService(), new FixedChecker()).CompareAsync(request, Baseline());

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task CompareAsync_EmbeddingOnlyModelDoesNotCallGenerator()
    {
        var client = new RecordingLlmClient(Response(EvidenceText, EvidenceText));

        var comparison = await new GroundedAnswerComparisonService(
            client, new SafetyRedactionService(), new FixedChecker(canGenerate: false))
            .CompareAsync(Request(), Baseline());

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Equal(0, client.CallCount);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("embedding-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_ReportsLocalGenerationTimeout()
    {
        var comparison = await new GroundedAnswerComparisonService(
            new TimeoutLlmClient(), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(Request(), Baseline());

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Null(comparison.Candidate);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("設定時間内", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_DistinguishesOutputTokenLimitFromMalformedJson()
    {
        var comparison = await new GroundedAnswerComparisonService(
            new RecordingLlmClient("{", "length"), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(Request(), Baseline());

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("出力トークン上限", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_DoesNotPromoteKnownToWorkToOfficialSupport()
    {
        const string sourceText = "Amazon Linux 2023 is known to work.";
        var client = new RecordingLlmClient(Response("Amazon Linux 2023は正式サポート対象です。", sourceText));
        var request = Request() with
        {
            Sources =
            [
                new SearchSource
                {
                    SourceId = "s1",
                    SourceType = "Manual",
                    ProductName = "Klocwork",
                    Title = "OS matrix",
                    Text = sourceText,
                },
            ],
            Case = new CaseContext { ProductName = "Klocwork" },
        };
        var baseline = Baseline() with
        {
            CustomerReplyDraft = "動作実績はあります。正式サポートは確認が必要です。",
            Readiness = "NeedsReview",
        };

        var comparison = await new GroundedAnswerComparisonService(
            client, new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, baseline);

        Assert.Equal(GroundedComparisonStatuses.Rejected, comparison.Status);
        Assert.Null(comparison.Candidate);
        Assert.Contains(comparison.Reasons, reason => reason.Contains("正式サポート", StringComparison.Ordinal));
    }

    private static AnswerDraftRequest Request() => new()
    {
        Case = new CaseContext { ProductName = "HelixQAC" },
        InquiryText = "設定画面で指定するポート番号を教えてください。",
        Sources =
        [
            new SearchSource
            {
                SourceId = "s1",
                SourceType = "Manual",
                ProductName = "HelixQAC",
                Title = "設定手順",
                Text = EvidenceText,
                Score = 0.9,
            },
            new SearchSource
            {
                SourceId = "unused",
                SourceType = "PastCaseNote",
                ProductName = "HelixQAC",
                Title = "unused source",
                Text = "別案件の情報",
                Score = 0.1,
            },
        ],
        Settings = new AiAssistantSettings { MaxPromptChars = 6000 },
    };

    private static AnswerDraftResult Baseline() => new()
    {
        CustomerReplyDraft = EvidenceText,
        Evidence = [new EvidenceItem { SourceId = "s1", SourceType = "Manual", Excerpt = EvidenceText }],
        Confidence = 0.8,
        Readiness = "CustomerReady",
        AnswerGenerationMode = AnswerGenerationModes.DeterministicWithPolishing,
    };

    private static string Response(string reply, string excerpt) => JsonSerializer.Serialize(new
    {
        customerReplyDraft = reply,
        internalMemo = "s1を確認しました。",
        needConfirmations = Array.Empty<object>(),
        evidence = new[] { new { sourceId = "s1", excerpt } },
        confidence = 0.8,
        warnings = Array.Empty<string>(),
    });

    private sealed class RecordingLlmClient(string response, string? doneReason = null) : ILlmClient
    {
        public int CallCount { get; private set; }
        public PromptMessages? LastPrompt { get; private set; }

        public Task<LlmGenerationResult> GenerateAsync(
            PromptMessages messages,
            LlmProviderSettings settings,
            bool disableThinking = true,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastPrompt = messages;
            return Task.FromResult(new LlmGenerationResult { Content = response, DoneReason = doneReason });
        }
    }

    private sealed class FixedChecker(bool canGenerate = true) : IOllamaConnectionChecker
    {
        public Task<OllamaConnectionCheckResult> CheckAsync(
            LlmProviderSettings settings,
            bool disableThinking = true,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OllamaModelCapabilityResult> CheckChatModelCapabilityAsync(
            LlmProviderSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OllamaModelCapabilityResult
            {
                CanGenerate = canGenerate,
                Message = canGenerate ? string.Empty : "embedding-only model",
            });
    }

    private sealed class TimeoutLlmClient : ILlmClient
    {
        public Task<LlmGenerationResult> GenerateAsync(
            PromptMessages messages,
            LlmProviderSettings settings,
            bool disableThinking = true,
            CancellationToken cancellationToken = default) =>
            throw new TaskCanceledException();
    }
}
