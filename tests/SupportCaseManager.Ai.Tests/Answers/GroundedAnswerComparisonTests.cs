using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Inquiries;
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
    public async Task CompareAsync_RejectsTraceableCitationContainingCustomerIdentity()
    {
        const string privateText = "(株)顧客会社の担当者様から問い合わせを受けました。";
        var request = Request() with
        {
            Sources = [new SearchSource
            {
                SourceId = "s1", SourceType = "PastCaseNote", ProductName = "HelixQAC",
                Text = privateText, Score = 0.9,
            }],
        };
        var baseline = Baseline() with
        {
            Evidence = [new EvidenceItem { SourceId = "s1", SourceType = "PastCaseNote", Excerpt = privateText }],
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "過去案件に問い合わせ記録があります。",
            internalMemo = "参考情報",
            needConfirmations = Array.Empty<object>(),
            evidence = new[] { new { sourceId = "s1", excerpt = privateText } },
            confidence = 0.3, warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, baseline);

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Single(result.CitationTraces);
        Assert.Contains(result.Reasons, reason => reason.Contains("顧客固有", StringComparison.Ordinal));
    }

    [Fact]
    public void GroundedPrompt_OffersOnlySafeRelevantCitationSpans()
    {
        var request = Request() with
        {
            InquiryText = "ポート番号1234の設定を確認したいです。",
            Sources = [new SearchSource
            {
                SourceId = "s1", SourceType = "Manual", ProductName = "HelixQAC",
                Text = "(株)顧客会社の担当者様から連絡がありました。\n" + EvidenceText +
                    "\nライセンスの別設定を変更してください。",
            }],
        };

        var prompt = GroundedAnswerPromptBuilder.Build(request, request.Sources);
        var choices = prompt.UserPrompt.Split('\n')
            .Where(static line => line.StartsWith("引用候補", StringComparison.Ordinal)).ToArray();
        Assert.Single(choices);
        Assert.Contains(EvidenceText, choices[0], StringComparison.Ordinal);
        Assert.DoesNotContain("顧客会社", choices[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareAsync_RejectsTraceableCitationWithoutClaimRelevance()
    {
        const string unrelated = "ライセンスの有効期限は管理画面で確認してください。";
        var request = Request() with
        {
            Sources = [new SearchSource
            {
                SourceId = "s1", SourceType = "Manual", ProductName = "HelixQAC",
                Text = unrelated,
            }],
        };
        var baseline = Baseline() with
        {
            Evidence = [new EvidenceItem { SourceId = "s1", SourceType = "Manual", Excerpt = unrelated }],
        };
        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(Response("ポート番号の確認が必要です。", unrelated)),
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(request, baseline);

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("関連性不足", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_DropsUnrelatedCitationFromSafeNonReadyReply()
    {
        const string unrelated = "ライセンスの有効期限は管理画面で確認してください。";
        var request = Request() with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
            Sources = [new SearchSource
            {
                SourceId = "s1", SourceType = "Manual", ProductName = "HelixQAC",
                Text = unrelated,
            }],
        };
        var baseline = Baseline() with
        {
            Evidence = [new EvidenceItem { SourceId = "s1", SourceType = "Manual", Excerpt = unrelated }],
            Readiness = "NeedsReview",
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: 設定画面のポート番号を質問されています。" +
                "現時点で断定できない事項: 指定値は確認できません。" +
                "追加で必要な確認: 対象製品版と設定画面の現在値を確認してください。",
            internalMemo = "別資料の引用は無関係",
            needConfirmations = new[] { new { question = "対象製品版と設定値を確認してください。", reason = "根拠不足", priority = "High" } },
            evidence = new[] { new { sourceId = "s1", excerpt = unrelated } },
            confidence = 0.2, warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, baseline);

        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, result.Status);
        Assert.Equal(1, result.GeneratedCitationCount);
        Assert.Single(result.CitationTraces);
        Assert.Empty(result.Candidate?.Evidence ?? []);
        Assert.Equal("NeedsReview", result.Candidate?.Readiness);
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
    public async Task CompareAsync_RemovesInquirySourceTagFromCustomerReply()
    {
        var request = Request() with
        {
            InquiryText = "VPN経由のライセンスサーバーで利用できますか。",
            FactResolution = new FactResolutionResult { AnswerReadiness = "InsufficientEvidence" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: VPN経由のライセンスサーバーを検討されています（sourceId=inquiry）。" +
                "現時点で断定できない事項: 利用可否は確認できません。" +
                "追加で必要な確認: 対象バージョンとメーカー見解を確認してください。",
            internalMemo = "顧客申告のみ",
            needConfirmations = new[] { new { question = "対象バージョンを確認してください。", reason = "根拠不足", priority = "High" } },
            evidence = Array.Empty<object>(), confidence = 0.2, warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline());

        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, result.Status);
        Assert.DoesNotContain("sourceId", result.Candidate?.CustomerReplyDraft);
    }

    [Fact]
    public async Task CompareAsync_NaturalizesCaseSourceTagInCustomerReply()
    {
        var request = Request() with
        {
            InquiryText = "VPN経由のライセンスサーバーで利用できますか。",
            FactResolution = new FactResolutionResult { AnswerReadiness = "InsufficientEvidence" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: VPN経由のライセンスサーバーを検討されています" +
                "（sourceId=case:abcd1234:line:29）。" +
                "現時点で断定できない事項: 利用可否は確認できません。" +
                "追加で必要な確認: 対象バージョンとメーカー見解を確認してください。",
            internalMemo = "案件履歴の識別子は社内用",
            needConfirmations = new[] { new { question = "対象バージョンを確認してください。", reason = "根拠不足", priority = "High" } },
            evidence = Array.Empty<object>(), confidence = 0.2, warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline());

        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, result.Status);
        Assert.Contains("案件履歴によると", result.Candidate?.CustomerReplyDraft);
        Assert.DoesNotContain("sourceId", result.Candidate?.CustomerReplyDraft);
    }

    [Fact]
    public async Task CompareAsync_DoesNotTreatNegatedOfficialSupportAsAffirmative()
    {
        var request = Request() with
        {
            InquiryText = "スキャンが処理待ち中です。原因を確認してください。",
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: スキャンが処理待ち中と報告されています。" +
                "現時点で断定できない事項: 原因や正式サポートの確定はできません。" +
                "追加で必要な確認: 実行時刻と関連ログを確認してください。",
            internalMemo = "根拠不足",
            needConfirmations = new[] { new { question = "実行時刻を確認してください。", reason = "原因未確定", priority = "High" } },
            evidence = Array.Empty<object>(), confidence = 0.2, warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline());

        Assert.DoesNotContain(result.Reasons, reason => reason.Contains("正式サポート", StringComparison.Ordinal));
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
    public async Task CompareAsync_AcceptsSpecificEvidenceFreeConfirmationListWithoutImperative()
    {
        var request = Request() with
        {
            InquiryText = "CxSASTのスキャンが処理待ち中から進みません。原因を調査してください。",
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: CxSASTのスキャンが処理待ち中と報告されています。" +
                "現時点で断定できない事項: 停滞原因は確認できません。" +
                "追加で必要な確認: スキャン実行時刻、処理サービスの稼働状態、同時刻の関連ログ。",
            internalMemo = "直接対応する資料なし",
            needConfirmations = new[] { new { question = "スキャン実行時刻と関連ログを確認してください。", reason = "原因未確定", priority = "High" } },
            evidence = Array.Empty<object>(),
            confidence = 0.2,
            warnings = Array.Empty<string>(),
        });

        var comparison = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline(), shadowReview: new ShadowReviewCriteria(
                ["CxJobManager停止後の復旧"], ["原因を断定しない"],
                ["CxJobManagerのログ"], "NeedsReview"));

        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, comparison.Status);
        Assert.Equal("NeedsReview", comparison.Candidate?.Readiness);
        Assert.Equal(0, comparison.GeneratedCitationCount);
        Assert.Equal(0, comparison.ExpectedClaimsCoverage);
    }

    [Fact]
    public async Task CompareAsync_PreservesKlocworkAbstentionWhenModelOmitsUncertaintyHeading()
    {
        var request = Request() with
        {
            Case = new CaseContext { ProductName = "Klocwork" },
            InquiryText = "ライセンスサーバーの移行先にVPN経由の社外サーバーを検討しています。",
            FactResolution = new FactResolutionResult { AnswerReadiness = "InsufficientEvidence" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: 顧客がVPN経由の社外サーバーを検討していること。" +
                "現時点でVPN経由の運用可否を断定できない。" +
                "追加で必要な確認: 対象バージョン、ライセンス方式、メーカーの対応見解。",
            internalMemo = "運用可否は未確認",
            needConfirmations = new[] { new { question = "対象バージョンを確認してください。", reason = "根拠不足", priority = "High" } },
            evidence = Array.Empty<object>(),
            confidence = 0.2,
            warnings = Array.Empty<string>(),
        });

        var comparison = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline(), shadowReview: new ShadowReviewCriteria(
                ["VPN経由の社外サーバーへ移行を検討している"], ["正式サポートと断定しない"],
                ["メーカー見解が必要"], "InsufficientEvidence"));

        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, comparison.Status);
        Assert.Equal("InsufficientEvidence", comparison.Candidate?.Readiness);
        Assert.Contains("現時点で断定できない事項", comparison.Candidate?.CustomerReplyDraft);
    }

    [Fact]
    public async Task CompareAsync_AllowsShadowAbstentionWhenNoSourceWasRetrieved()
    {
        var request = Request() with
        {
            Case = new CaseContext { ProductName = "Klocwork" },
            InquiryText = "2025.2から2026.2へ更新します。チェッカーのenabled既定設定に変更はありますか。",
            Sources = [],
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsManufacturerConfirmation" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: 2025.2から2026.2への更新時にチェッカー設定の変更有無を質問されています。" +
                "現時点で断定できない事項: enabled既定設定の変更有無は確認できません。" +
                "追加で必要な確認: 両版のチェッカー構成差分とメーカー見解。",
            internalMemo = "参照資料なし",
            needConfirmations = new[] { new { question = "メーカー見解を確認してください。", reason = "根拠不足", priority = "High" } },
            evidence = Array.Empty<object>(),
            confidence = 0.2,
            warnings = Array.Empty<string>(),
        });

        var prompt = GroundedAnswerPromptBuilder.Build(request, []);
        Assert.Contains("検索Evidenceは0件", prompt.UserPrompt);
        Assert.Contains("公式資料を確認した事実として書かない", prompt.UserPrompt);

        var comparison = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [] },
                shadowReview: new ShadowReviewCriteria(
                    ["2026.2のenabled変更有無は未確認"], ["変更なしと断定しない"],
                    ["2025.2/2026.2の構成差分"], "NeedsManufacturerConfirmation"));

        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, comparison.Status);
        Assert.Equal("NeedsManufacturerConfirmation", comparison.Candidate?.Readiness);
        Assert.Empty(comparison.CitationTraces);
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
        Assert.Equal("CustomerReady", comparison.RawGeneratedReadiness);
        Assert.Equal("NeedsReview", comparison.GeneratedReadiness);
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
        Assert.Contains("引用可能な直接対応資料: なし", prompt.UserPrompt);
        Assert.Contains("evidenceは空配列", prompt.UserPrompt);
        Assert.Contains("needConfirmationsだけに確認項目を書き", prompt.SystemPrompt);
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
    public void GroundedPrompt_RequestsSelectedOfficialFactInCustomerReplyWithItsCondition()
    {
        var source = Request().Sources[0] with
        {
            SourceId = "official:checker",
            SourceType = "OfficialDoc",
            Text = "If you migrate projects_root, verify the same checker configuration before the first integration build.",
        };
        var request = Request() with
        {
            InquiryText = "チェッカー設定の既定enabledに変更はありますか。",
            Sources = [source],
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "SelectedOfficialStatement", Status = "Confirmed", SourceType = "OfficialDoc",
                    EvidenceId = source.SourceId,
                    Value = source.Text,
                    Statement = "選択資料の原文記述",
                }],
            },
        };

        var prompt = GroundedAnswerPromptBuilder.Build(request, [source]);

        Assert.Contains("Confirmed Fact: 選択資料の原文記述", prompt.UserPrompt);
        Assert.Contains("internalMemoだけに留めない", prompt.UserPrompt);
        Assert.Contains("未確認の版・既定値", prompt.UserPrompt);
        Assert.Contains("生成契約（customerReplyDraft）", prompt.UserPrompt);
        Assert.Contains("条件と出典を明示", prompt.UserPrompt);
        Assert.Contains("Must Preserve Fact: sourceType=OfficialDoc; attribution=選択した公式資料", prompt.UserPrompt);
        Assert.Contains("timing=first integration buildより前", prompt.UserPrompt);
    }

    [Fact]
    public async Task CompareAsync_AcceptsConcreteVersionCheckInNonReadyReply()
    {
        var request = Request() with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: ポート番号1234の設定資料があります。" +
                "現時点で断定できない事項: お客様の環境への適用は不明です。" +
                "追加で必要な確認: 使用している製品のバージョンと設定内容。",
            internalMemo = "版の照合が必要です。",
            needConfirmations = new[] { new { question = "使用している製品のバージョンは何ですか。", reason = "適用範囲", priority = "High" } },
            evidence = Array.Empty<object>(),
            confidence = 0.3,
            warnings = Array.Empty<string>(),
        });

        var comparison = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Readiness = "NeedsReview" });

        Assert.DoesNotContain(comparison.Reasons,
            reason => reason.Contains("具体的な追加確認がありません", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_PreservesInquiryVersionFromBaselineAsCustomerContext()
    {
        var request = Request() with
        {
            InquiryText = "2024.1のポート設定を確認したいです。",
            InquiryFocus = new InquiryFocus { TargetVersions = ["2024.1"] },
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var baseline = Baseline() with
        {
            CustomerReplyDraft = "2024.1のポート設定を確認します。",
            Readiness = "NeedsReview",
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: ポート番号1234の資料があります。" +
                "現時点で断定できない事項: 設定の適用は不明です。" +
                "追加で必要な確認: 現在の設定値を確認してください。",
            internalMemo = "版は問い合わせに記載されています。",
            needConfirmations = Array.Empty<object>(),
            evidence = Array.Empty<object>(),
            confidence = 0.3,
            warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, baseline);

        Assert.Contains("記載版=2024.1", result.GeneratedReplyDraft);
        Assert.DoesNotContain(result.Reasons,
            reason => reason.Contains("必須値の欠落", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_RejectsDenialOfHeadingPresentInSelectedOfficialSource()
    {
        const string sourceText = "Klocwork 2026.2 Release notes. Disabled checkers If you migrate the projects_root, verify the checker configuration before the first integration build.";
        var request = Request() with
        {
            InquiryText = "2026.2のDisabled checkersについて確認したいです。",
            Sources = [new SearchSource { SourceId = "official:release", SourceType = "OfficialDoc", Text = sourceText }],
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsManufacturerConfirmation" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: 2026.2には「Disabled checkers」セクションが存在せず。" +
                "現時点で断定できない事項: 既定値は未確認です。" +
                "追加で必要な確認: checker設定を確認してください。",
            internalMemo = "見出しなし",
            needConfirmations = Array.Empty<object>(),
            evidence = Array.Empty<object>(),
            confidence = 0.3,
            warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with
            {
                Readiness = "NeedsManufacturerConfirmation",
                Evidence = [new EvidenceItem
                {
                    SourceId = "official:release", SourceType = "OfficialDoc", Excerpt = sourceText,
                }],
            });

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("見出しを不存在", StringComparison.Ordinal));
    }

    [Fact]
    public void GroundedPrompt_KeepsCaseObservationSeparateAndConstrainsCitationPairs()
    {
        var request = Request() with
        {
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsReview",
                ResolvedFacts = [new ResolvedFact
                {
                    Status = "Candidate", SourceType = "CurrentCase",
                    Value = "前回の設定変更後は一週間再発しなかった。",
                    EvidenceId = "case:line:12", Statement = "案件履歴の観測",
                }],
            },
        };

        var prompt = GroundedAnswerPromptBuilder.Build(request, request.Sources);

        Assert.Contains("CurrentCase observation (not product specification)", prompt.UserPrompt);
        Assert.Contains("前回の設定変更後は一週間再発しなかった", prompt.UserPrompt);
        Assert.Contains("case:line:12", prompt.UserPrompt);
        Assert.DoesNotContain("Confirmed Fact: 案件履歴の観測", prompt.UserPrompt);
        Assert.Contains("sourceIdや内部識別子を記載しない", prompt.SystemPrompt);
        var alternatives = prompt.OutputSchema!.Value.GetProperty("properties").GetProperty("evidence")
            .GetProperty("items").GetProperty("oneOf");
        Assert.NotEmpty(alternatives.EnumerateArray());
        Assert.All(alternatives.EnumerateArray(), item =>
            Assert.Equal("s1", item.GetProperty("properties").GetProperty("sourceId")
                .GetProperty("const").GetString()));
    }

    [Fact]
    public async Task CompareAsync_RejectsUnattributedDocumentClaimWithoutEvidence()
    {
        var request = Request() with
        {
            Sources = [],
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsManufacturerConfirmation" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: リリースノートには既定設定の変更なしと記載されています。" +
                "現時点で断定できない事項: 製品仕様は確認できません。" +
                "追加で必要な確認: メーカー見解を確認してください。",
            internalMemo = "根拠未確認",
            needConfirmations = new[] { new { question = "メーカー見解を確認してください。", reason = "資料未確認", priority = "High" } },
            evidence = Array.Empty<object>(), confidence = 0.2, warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [] },
                shadowReview: new ShadowReviewCriteria([], [], [], "NeedsManufacturerConfirmation"));

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("資料の記載", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_AllowsAttributedCustomerReportWithoutEvidence()
    {
        var request = Request() with
        {
            InquiryText = "顧客からKlocwork 2026.1と2026.2のリリースノートに記載違いがあるとの説明です。Klocwork 2026.2のチェッカー構成ファイルのenabled既定設定に変更はありますか。",
            Sources = [],
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsManufacturerConfirmation" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: 顧客が報告したリリースノートの記載違いについて照会されています。" +
                "現時点で断定できない事項: enabled既定設定の変更有無は確認できません。" +
                "追加で必要な確認: チェッカー構成ファイルの版間差分とメーカー見解を確認してください。",
            internalMemo = "根拠未確認",
            needConfirmations = new[] { new { question = "メーカー見解を確認してください。", reason = "仕様未確認", priority = "High" } },
            evidence = Array.Empty<object>(), confidence = 0.2, warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { CustomerReplyDraft = "確認中です。", Evidence = [] },
                shadowReview: new ShadowReviewCriteria(["Klocwork 2026.2のenabled変更有無は未確認"],
                    [], ["メーカー見解"], "NeedsManufacturerConfirmation"));

        Assert.True(result.Status == GroundedComparisonStatuses.ReadyForHumanReview,
            string.Join(" | ", result.Reasons));
        Assert.Equal("NeedsManufacturerConfirmation", result.Candidate?.Readiness);
    }

    [Fact]
    public async Task CompareAsync_RejectsReversedCustomerNegationWithoutEvidence()
    {
        var request = Request() with
        {
            InquiryText = "チェッカー構成ファイルのenabledフィールドに追加されたチェッカーはありません。変更有無を確認してください。",
            Sources = [],
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsManufacturerConfirmation" },
        };
        var prompt = GroundedAnswerPromptBuilder.Build(request, []);
        Assert.Contains("追加されたチェッカーはありません", prompt.UserPrompt);
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: お客様によると追加されたチェッカーが記載されていました。" +
                "現時点で断定できない事項: 変更有無は確認できません。" +
                "追加で必要な確認: enabledフィールドの変更履歴とメーカー見解を確認してください。",
            internalMemo = "顧客申告",
            needConfirmations = new[] { new { question = "メーカー見解を確認してください。", reason = "根拠不足", priority = "High" } },
            evidence = Array.Empty<object>(), confidence = 0.2, warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { CustomerReplyDraft = "確認中です。", Evidence = [] },
                shadowReview: new ShadowReviewCriteria([], [], [], "NeedsManufacturerConfirmation"));

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("否定表現", StringComparison.Ordinal));
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

    [Fact]
    public async Task CompareAsync_RejectsStartupFailureReversedWithSelectedEvidence()
    {
        var request = Request() with
        {
            InquiryText = "Validateサーバー を起動できません:データベースサーバー は別のプロジェクトルートで実行中です。",
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: Validateサーバーが同一マシンで起動し、" +
                "データベースサーバーは別のプロジェクトルートで動作中です。" +
                "現時点で断定できない事項: 設定関係は不明です。" +
                "追加で必要な確認: 両サーバーのプロジェクトルートを確認してください。",
            internalMemo = "関係確認", needConfirmations = Array.Empty<object>(),
            evidence = Array.Empty<object>(), confidence = 0.2, warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Readiness = "NeedsReview" });

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("起動失敗", StringComparison.Ordinal));
    }

    [Fact]
    public void GroundedPrompt_PrioritizesPastObservationAndPreservesVersionRolesAndTiming()
    {
        var source = Request().Sources[0] with
        {
            SourceId = "official:checker", SourceType = "OfficialDoc",
            Text = "If you chose to migrate your projects_root directory, verify that you have the same checker configuration as the previous release before your first integration build analysis.",
        };
        var request = Request() with
        {
            InquiryText = "現在利用中のv2025.2からv2026.2へのバージョンアップを計画中。2026.1のチェッカー設定と比較しています。",
            Sources = [source],
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = [
                    new ResolvedFact { Status = "Confirmed", SourceType = "OfficialDoc",
                        EvidenceId = source.SourceId, Value = source.Text, Statement = "公式原文" },
                    new ResolvedFact { Status = "Candidate", SourceType = "CurrentCase",
                        Key = "PriorCaseOutcome", EvidenceId = "case:line:10",
                        Value = "設定変更後、約1週間再発しなかった。" },
                ],
            },
        };

        var prompt = GroundedAnswerPromptBuilder.Build(request, [source]);
        Assert.Contains("現在版・移行元=2025.2; 移行先=2026.2", prompt.UserPrompt);
        Assert.Contains("比較資料に言及した版=2026.1", prompt.UserPrompt);
        Assert.Contains("Confirmed Fact timing sourceId=official:checker", prompt.UserPrompt);
        Assert.Contains("first integration build analysisより前", prompt.UserPrompt);
        Assert.Contains("約1週間再発しなかった", prompt.UserPrompt);
        Assert.Contains("恒久対策や今回の原因として断定しない", prompt.UserPrompt);
    }

    [Fact]
    public void GroundedPrompt_PutsSelectedOfficialVersionAndHeadingBeforeConflictingInquiry()
    {
        const string text = "Klocwork Documentation | 2026.2 Release notes. Disabled checkers If you migrate projects_root, verify the same checker configuration before your first integration build analysis.";
        var source = Request().Sources[0] with { SourceId = "official:release", SourceType = "OfficialDoc", Text = text };
        var request = Request() with
        {
            InquiryText = "2026.2のリリースノートにDisabled checkersがないと聞きました。",
            Sources = [source],
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "SelectedOfficialStatement", Status = "Confirmed", SourceType = "OfficialDoc",
                    EvidenceId = source.SourceId, Value = text,
                    Statement = "選択した公式資料に見出し『Disabled checkers』が存在する。",
                }],
            },
        };

        var prompt = GroundedAnswerPromptBuilder.Build(request, [source]);
        Assert.True(prompt.UserPrompt.IndexOf("公式原文の照合結果", StringComparison.Ordinal) <
            prompt.UserPrompt.IndexOf("問い合わせ:", StringComparison.Ordinal));
        Assert.Contains("2026.2 Release notesに「Disabled checkers」が実在する", prompt.UserPrompt);
        Assert.Contains("before your first integration build", prompt.UserPrompt);
    }

    [Fact]
    public async Task CompareAsync_PreservesObservedPriorOutcomeWithoutInferringAttachmentContents()
    {
        var request = Request() with
        {
            Sources = [],
            Settings = new AiAssistantSettings { MaxPromptChars = 12000 },
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "InsufficientEvidence",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "PriorCaseOutcome", Status = "Candidate", SourceType = "CurrentCase",
                    EvidenceId = "case:history:1",
                    Value = "ユーザーデータ領域変更後、約1週間再発しなかった。",
                }],
            },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: 設定変更の相談です。" +
                "現時点で断定できない事項: 原因は確認できません。" +
                "追加で必要な確認: 添付画像とXMLの内容を確認してください。",
            internalMemo = "添付は未読", evidence = Array.Empty<object>(),
            needConfirmations = new[] { new { question = "添付内容を確認してください。", reason = "未確認", priority = "High" } },
            confidence = 0.2, warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "InsufficientEvidence" },
                shadowReview: new ShadowReviewCriteria([], [], [], "InsufficientEvidence"));

        Assert.True(result.GeneratedReplyDraft?.Contains("ユーザーデータ領域変更後、約1週間再発しなかった", StringComparison.Ordinal) == true,
            string.Join(" | ", result.Reasons));
        Assert.Contains("当時の観測", result.GeneratedReplyDraft);
        Assert.DoesNotContain("画像には", result.GeneratedReplyDraft);
    }

    [Theory]
    [InlineData("Klocwork 2026.1のリリースノートには「Disabled checkers」があり、初回integration build前に確認します。")]
    [InlineData("Klocwork 2026.2のリリースノートには「Disabled checkers」の記載が見られず、初回integration build前に確認します。")]
    public async Task CompareAsync_RejectsOfficialVersionHeadingOrTimingContradiction(string claim)
    {
        const string sourceText = "Klocwork Documentation | 2026.2 Release notes. Disabled checkers If you migrate projects_root, verify the same checker configuration before your first integration build analysis.";
        var source = Request().Sources[0] with
        {
            SourceId = "official:release", SourceType = "OfficialDoc", Text = sourceText,
        };
        var request = Request() with
        {
            Sources = [source],
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "SelectedOfficialStatement", Status = "Confirmed", SourceType = "OfficialDoc",
                    EvidenceId = source.SourceId, Value = sourceText,
                    Statement = "選択した公式資料に見出し『Disabled checkers』が存在する。",
                }],
            },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: " + claim +
                "現時点で断定できない事項: 既定値は確認できません。" +
                "追加で必要な確認: メーカーに設定変更有無を確認してください。",
            internalMemo = "版確認", evidence = Array.Empty<object>(),
            needConfirmations = new[] { new { question = "メーカー確認", reason = "未確認", priority = "High" } },
            confidence = 0.2, warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Readiness = "NeedsManufacturerConfirmation",
                Evidence = [new EvidenceItem { SourceId = source.SourceId, SourceType = source.SourceType,
                    Excerpt = source.Text }] });

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("版・見出し・確認時点", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_PreservesVerifiedOfficialTimingWhenModelOmitsIt()
    {
        const string statement = "Disabled checkers If you migrate projects_root, verify the same checker configuration before your first integration build analysis.";
        var source = Request().Sources[0] with
        {
            SourceId = "official:release", SourceType = "OfficialDoc",
            Text = "Klocwork Documentation | 2026.2 Release notes. " + statement,
        };
        var request = Request() with
        {
            Sources = [source],
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "SelectedOfficialStatement", Status = "Confirmed", SourceType = "OfficialDoc",
                    EvidenceId = source.SourceId, Value = statement,
                    Statement = "選択した公式資料に見出し『Disabled checkers』が存在する。",
                }],
            },
        };
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "確認できる事実: Klocwork 2026.2 Release notesに「Disabled checkers」があります。" +
                "現時点で断定できない事項: 既定値変更の有無は公式資料に記載がありません。" +
                "追加で必要な確認: メーカーに版間差分を確認してください。",
            internalMemo = "版確認", evidence = Array.Empty<object>(),
            needConfirmations = new[] { new { question = "メーカーに確認してください。", reason = "未確認", priority = "High" } },
            confidence = 0.2, warnings = Array.Empty<string>(),
        });

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(response), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Readiness = "NeedsManufacturerConfirmation",
                Evidence = [new EvidenceItem { SourceId = source.SourceId, SourceType = source.SourceType,
                    Excerpt = source.Text }] });

        Assert.Contains("before your first integration build", result.GeneratedReplyDraft);
        Assert.Contains("今回選択した資料では確認できません", result.GeneratedReplyDraft);
        Assert.DoesNotContain("公式資料に記載がありません", result.GeneratedReplyDraft);
        Assert.DoesNotContain(result.Reasons,
            reason => reason.Contains("版・見出し・確認時点", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_KeepsInquiryStartupFailurePolarityInFactResolution()
    {
        var request = NonReadyRequest("Validateサーバー を起動できません。データベースサーバーは実行中です。") with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var client = new RecordingLlmClient(NonReadyResponse(
            "確認できる事実: Validateサーバーが起動し、データベースサーバーは実行中です。" +
            "現時点で断定できない事項: 原因は確認できません。" +
            "追加で必要な確認: 起動ログを確認してください。"));

        var result = await new GroundedAnswerComparisonService(client,
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(request,
            Baseline() with { Evidence = [], Readiness = "NeedsReview" },
            shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("起動失敗", StringComparison.Ordinal));
        Assert.Contains("sourceType=CurrentInquiry; polarity=失敗", client.LastPrompt!.UserPrompt);
        Assert.Contains("operation=起動", client.LastPrompt.UserPrompt);
    }

    [Fact]
    public async Task CompareAsync_AllowsFailedStartupWhenEntityAndPolarityArePreserved()
    {
        var request = NonReadyRequest("Validateサーバー を起動できません。データベースサーバーは実行中です。") with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var reply = "確認できる事実: お客様の報告ではValidateサーバーを起動できません。" +
            "データベースサーバーは実行中です。" +
            "現時点で断定できない事項: 原因は確認できません。" +
            "追加で必要な確認: 両サーバーの設定値と起動ログを確認してください。";

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(NonReadyResponse(reply)), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "NeedsReview" },
                shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.DoesNotContain(result.Reasons, reason => reason.Contains("重要Fact", StringComparison.Ordinal));
        Assert.Equal(reply, result.GeneratedReplyDraft);
    }

    [Fact]
    public async Task CompareAsync_RejectsOmittedRecoveryAndMqLogFacts()
    {
        var request = NonReadyRequest("スキャンが処理待ち中から進みません。") with
        {
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsReview",
                ResolvedFacts = [
                    new ResolvedFact { Key = "CaseObservation", Status = "Candidate",
                        SourceType = "CurrentCase", EvidenceId = "case:history:1",
                        Value = "CxJobManager and CxSystemManager were stopped. After starting both services, they confirmed that the scan completed successfully." },
                    new ResolvedFact { Key = "ObservedLogFailure", Status = "Candidate",
                        SourceType = "CurrentCase", EvidenceId = "case:log:1",
                        Value = "添付ログにResultsReceiverのMQ接続失敗が記録されている。" },
                ],
            },
        };
        var client = new RecordingLlmClient(NonReadyResponse(
            "確認できる事実: お客様はスキャン停滞を報告しています。" +
            "現時点で断定できない事項: 原因は確認できません。" +
            "追加で必要な確認: サービス状態とログを確認してください。"));

        var result = await new GroundedAnswerComparisonService(client,
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(request,
            Baseline() with { Evidence = [], Readiness = "NeedsReview" },
            shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("復旧履歴", StringComparison.Ordinal));
        Assert.Contains("polarity=停止→起動後に完了", client.LastPrompt!.UserPrompt);
        Assert.Contains("sourceType=CurrentCase; polarity=失敗", client.LastPrompt.UserPrompt);
    }

    [Fact]
    public async Task CompareAsync_RejectsOmittedMigrationOperationAndEndpoints()
    {
        var request = NonReadyRequest("社内のプライベートクラウドから、AzureのVMへの移設を検討しています。") with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "InsufficientEvidence" },
        };
        var client = new RecordingLlmClient(NonReadyResponse(
            "確認できる事実: Azure Managed Instanceを検討されています。" +
            "現時点で断定できない事項: 対応可否は確認できません。" +
            "追加で必要な確認: 製品の版と構成を確認してください。"));

        var result = await new GroundedAnswerComparisonService(client,
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(request,
            Baseline() with { Evidence = [], Readiness = "InsufficientEvidence" },
            shadowReview: new ShadowReviewCriteria([], [], [], "InsufficientEvidence"));

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("移設元・移設先", StringComparison.Ordinal));
        Assert.Contains("operation=移設", client.LastPrompt!.UserPrompt);
        Assert.Contains("sourceType=CurrentInquiry", client.LastPrompt.UserPrompt);
    }

    [Fact]
    public async Task CompareAsync_RejectsOmittedVersionedCaseHistoryWithoutPromotingItsSource()
    {
        var request = NonReadyRequest("検出された指摘は誤検知でしょうか。") with
        {
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "CaseObservation", Status = "Candidate", SourceType = "CurrentCase",
                    EvidenceId = "case:history:57",
                    Value = "メーカーより、本件につきましてはCheckmarx SAST バージョン 9.7.7にて不具合が修正されたとの回答がございました。",
                }],
            },
        };
        var client = new RecordingLlmClient(NonReadyResponse(
            "確認できる事実: お客様は検出結果を相談されています。" +
            "現時点で断定できない事項: 誤検知かどうかは確認できません。" +
            "追加で必要な確認: メーカーの回答原文を確認してください。"));

        var result = await new GroundedAnswerComparisonService(client,
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(request,
            Baseline() with { Evidence = [], Readiness = "NeedsManufacturerConfirmation" },
            shadowReview: new ShadowReviewCriteria([], [], [], "NeedsManufacturerConfirmation"));

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("版付き修正記録", StringComparison.Ordinal));
        Assert.Contains("version=9.7.7", client.LastPrompt!.UserPrompt);
        Assert.Contains("sourceType=CurrentCase", client.LastPrompt.UserPrompt);
        Assert.Contains("メーカー原文未確認", client.LastPrompt.UserPrompt);
    }

    [Fact]
    public async Task CompareAsync_AllowsAttributedRecoveryAndMqObservation()
    {
        var request = NonReadyRequest("スキャンが処理待ち中から進みません。") with
        {
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsReview",
                ResolvedFacts = [
                    new ResolvedFact { Key = "CaseObservation", Status = "Candidate",
                        SourceType = "CurrentCase", EvidenceId = "case:history:1",
                        Value = "CxJobManager and CxSystemManager were stopped. After starting both services, they confirmed that the scan completed successfully." },
                    new ResolvedFact { Key = "ObservedLogFailure", Status = "Candidate",
                        SourceType = "CurrentCase", EvidenceId = "case:log:1",
                        Value = "添付ログにResultsReceiverのMQ接続失敗が記録されている。" },
                ],
            },
        };
        var reply = "確認できる事実: 案件履歴にはCxJobManagerとCxSystemManagerが停止し、" +
            "両サービス起動後にスキャンが完了したと記録されています。" +
            "添付ログにはResultsReceiverのMQ接続失敗が記録されています。" +
            "現時点で断定できない事項: 根本原因は確認できません。" +
            "追加で必要な確認: 再発時のサービス状態とログを確認してください。";

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(NonReadyResponse(reply)), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "NeedsReview" },
                shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.DoesNotContain(result.Reasons, reason => reason.Contains("重要Fact", StringComparison.Ordinal));
        Assert.Equal(reply, result.GeneratedReplyDraft);
    }

    [Fact]
    public async Task CompareAsync_RejectsMigrationPlanPresentedAsCompleted()
    {
        var request = NonReadyRequest("社内のプライベートクラウドから、AzureのVMへの移設を検討しています。") with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "InsufficientEvidence" },
        };
        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(NonReadyResponse(
                "確認できる事実: プライベートクラウドからAzureのVMへ移設しました。" +
                "現時点で断定できない事項: 対応可否は確認できません。" +
                "追加で必要な確認: 製品の版と構成を確認してください。")),
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(request,
            Baseline() with { Evidence = [], Readiness = "InsufficientEvidence" },
            shadowReview: new ShadowReviewCriteria([], [], [], "InsufficientEvidence"));

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("検討中の移設", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_RejectsVersionedFixNegationEvenWithVersionPresent()
    {
        var request = NonReadyRequest("検出された指摘は誤検知でしょうか。") with
        {
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "CaseObservation", Status = "Candidate", SourceType = "CurrentCase",
                    EvidenceId = "case:history:57",
                    Value = "メーカーより、バージョン 9.7.7にて不具合が修正されたとの回答がございました。",
                }],
            },
        };
        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(NonReadyResponse(
                "確認できる事実: 案件履歴には9.7.7で不具合は修正されていないと記録されています。" +
                "現時点で断定できない事項: 原因は確認できません。" +
                "追加で必要な確認: メーカー原文を確認してください。")),
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(request,
            Baseline() with { Evidence = [], Readiness = "NeedsManufacturerConfirmation" },
            shadowReview: new ShadowReviewCriteria([], [], [], "NeedsManufacturerConfirmation"));

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("極性反転", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareAsync_AllowsMigrationPlanWithSourceAndDestination()
    {
        var request = NonReadyRequest("社内のプライベートクラウドから、AzureのVMへの移設を検討しています。") with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "InsufficientEvidence" },
        };
        var reply = "確認できる事実: お客様はプライベートクラウドからAzureのVMへの移設を検討しています。" +
            "現時点で断定できない事項: 構成の対応可否は確認できません。" +
            "追加で必要な確認: 製品の版と移設先の構成を確認してください。";

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(NonReadyResponse(reply)), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "InsufficientEvidence" },
                shadowReview: new ShadowReviewCriteria([], [], [], "InsufficientEvidence"));

        Assert.DoesNotContain(result.Reasons, reason => reason.Contains("重要Fact", StringComparison.Ordinal));
        Assert.Equal(reply, result.GeneratedReplyDraft);
    }

    [Fact]
    public async Task CompareAsync_AllowsVersionedFixWhenAttributedToCaseHistory()
    {
        var request = NonReadyRequest("検出された指摘は誤検知でしょうか。") with
        {
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "CaseObservation", Status = "Candidate", SourceType = "CurrentCase",
                    EvidenceId = "case:history:57",
                    Value = "メーカーより、バージョン 9.7.7にて不具合が修正されたとの回答がございました。",
                }],
            },
        };
        var reply = "確認できる事実: 案件履歴には9.7.7で不具合が修正されたとの記録があります。" +
            "現時点で断定できない事項: メーカー原文は未確認で、誤検知かは断定できません。" +
            "追加で必要な確認: メーカー回答原文と再検証結果を確認してください。";

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(NonReadyResponse(reply)), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "NeedsManufacturerConfirmation" },
                shadowReview: new ShadowReviewCriteria([], [], [], "NeedsManufacturerConfirmation"));

        Assert.DoesNotContain(result.Reasons, reason => reason.Contains("重要Fact", StringComparison.Ordinal));
        Assert.Equal(reply.Replace("記録があります。", "記録があります（メーカー原文未確認の案件履歴です）。", StringComparison.Ordinal), result.GeneratedReplyDraft);
    }

    [Fact]
    public async Task GroundedPrompt_MustPreserveStartupFailureOverAmbiguousInquiryWording()
    {
        var request = NonReadyRequest("Validateサーバーを起動したのですが、エラーでValidateサーバーを起動できません。") with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var client = new RecordingLlmClient(NonReadyResponse(
            "確認できる事実: お客様はValidateサーバーを起動できないと報告しています。" +
            "現時点で断定できない事項: 原因は未確認です。" +
            "追加で必要な確認: 起動ログと設定値を確認してください。"));

        await new GroundedAnswerComparisonService(client, new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "NeedsReview" },
                shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        var prompt = client.LastPrompt!.UserPrompt;
        Assert.Equal(1, prompt.Split("生成契約（customerReplyDraft）").Length - 1);
        Assert.Contains("Must Preserve Facts:", prompt);
        Assert.Contains("sourceType=CurrentInquiry; polarity=失敗", prompt);
        Assert.Contains("customerMeaning=Validateサーバーは起動できない", prompt);
        Assert.Contains("各見出しの直後に", prompt);
        Assert.Contains("見出し名だけの出力は禁止", prompt);
        Assert.Contains("『確認できる事実:』『現時点で断定できない事項:』", prompt);
        Assert.DoesNotContain("customerReplyDraftは必ず次の3行", prompt);
        Assert.DoesNotContain("顧客申告・CurrentCase観測と、選択資料のConfirmed Factを区別して記す", prompt);
        Assert.DoesNotContain("現時点で断定できない事項: 未確認の仕様・原因・可否", prompt);
        Assert.DoesNotContain("対象バージョン、設定値、構成差分、関連ログ、メーカー見解など問い合わせに必要な具体項目", prompt);
        Assert.True(prompt.LastIndexOf("Must Preserve Fact:", StringComparison.Ordinal) >
            prompt.IndexOf("生成契約（customerReplyDraft）", StringComparison.Ordinal));
    }

    [Fact]
    public void GroundedPrompt_MustPreserveInquiryVersionRecoveryAndLogObservation()
    {
        const string inquiry = "新環境にCxSAST（9.7.4.1001 HF5）をインストール後、スキャンが処理待ち中から進みません。";
        var request = NonReadyRequest(inquiry) with
        {
            InquiryFocus = new InquiryFocusExtractor().Extract(inquiry),
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsReview",
                ResolvedFacts = [
                    new ResolvedFact { Key = "CaseObservation", Status = "Candidate",
                        SourceType = "CurrentCase", EvidenceId = "case:history:1",
                        Value = "CxJobManager and CxSystemManager were stopped. After starting both services, they confirmed that the scan completed successfully." },
                    new ResolvedFact { Key = "ObservedLogFailure", Status = "Candidate",
                        SourceType = "CurrentCase", EvidenceId = "case:log:1",
                        Value = "添付ログにResultsReceiverのMQ接続失敗が記録されている。" },
                ],
            },
        };

        var prompt = GroundedAnswerPromptBuilder.Build(request, []).UserPrompt;
        var mustPreserve = prompt[prompt.IndexOf("生成契約（customerReplyDraft）", StringComparison.Ordinal)..];
        Assert.Contains("version=9.7.4.1001 HF5", mustPreserve);
        Assert.Contains("CxJobManagerとCxSystemManagerが停止し、両サービスの起動後にスキャン完了", mustPreserve);
        Assert.Contains("ResultsReceiverのMQ接続失敗", mustPreserve);
        Assert.Contains("既知Factを一般的なUnknownで上書きしない", mustPreserve);
        Assert.Contains("sourceType=CurrentInquiry; attribution=顧客申告", mustPreserve);
        Assert.Contains("sourceType=CurrentCase; polarity=失敗", mustPreserve);
        Assert.DoesNotContain("次の3行を具体的に埋めてください", mustPreserve);
        Assert.Contains("再発時のキューを処理するサービスの稼働状態", prompt);
    }

    [Fact]
    public void GroundedPrompt_MustPreserveVersionedFixAsAttributedCaseHistory()
    {
        const string inquiry = "CxSAST 9.7.2 HF1で検出されたＣＲＯＳヘッダの指摘は誤検知でしょうか。";
        var request = NonReadyRequest(inquiry) with
        {
            InquiryFocus = new InquiryFocusExtractor().Extract(inquiry),
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "CaseObservation", Status = "Candidate", SourceType = "CurrentCase",
                    EvidenceId = "case:history:57",
                    Value = "メーカーより、本件につきましてはCheckmarx SAST バージョン 9.7.7にて不具合が修正されたとの回答がございました。",
                }],
            },
        };

        var prompt = GroundedAnswerPromptBuilder.Build(request, []).UserPrompt;
        var mustPreserve = prompt[prompt.IndexOf("生成契約（customerReplyDraft）", StringComparison.Ordinal)..];
        Assert.Contains("version=9.7.2", mustPreserve);
        Assert.Contains("sourceType=CurrentCase; polarity=案件履歴に修正の記録・メーカー原文未確認; version=9.7.7", mustPreserve);
        Assert.Contains("メーカー回答があったという案件記録。メーカー原文は未確認", mustPreserve);
        Assert.Contains("attribution=メーカー原文未確認のCurrentCase案件履歴", mustPreserve);
        Assert.Contains("ＣＲＯＳヘッダ", prompt);
        Assert.DoesNotContain("CR-Oヘッダ", prompt);
        Assert.DoesNotContain("sourceType=OfficialDoc", mustPreserve);
    }

    [Fact]
    public async Task CompareAsync_RevisesHeadingOnlyReplyOnceAndRunsFullValidation()
    {
        var inquiry = "VPN経由の社外ライセンスサーバーへの移行を検討しています。";
        var request = NonReadyRequest(inquiry) with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "InsufficientEvidence" },
        };
        var client = new SequenceLlmClient(
            NonReadyResponse("確認できる事実 / 現時点で断定できない事項 / 追加で必要な確認"),
            NonReadyResponse("確認できる事実: お客様はVPN経由の社外ライセンスサーバーへの移行を検討しています。" +
                "現時点で断定できない事項: この構成で運用できるかは確認できません。" +
                "追加で必要な確認: 対象バージョン、ライセンス方式、メーカーの対応見解を確認してください。"));

        var result = await new GroundedAnswerComparisonService(
            client, new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "InsufficientEvidence" },
                shadowReview: new ShadowReviewCriteria([], [], [], "InsufficientEvidence"));

        Assert.Equal(2, client.CallCount);
        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, result.Status);
        Assert.Equal("InsufficientEvidence", result.Candidate?.Readiness);
        Assert.Contains("見出しだけ", client.Prompts[1].UserPrompt);
        Assert.Equal(client.Prompts[0].OutputSchema?.GetRawText(), client.Prompts[1].OutputSchema?.GetRawText());
    }

    [Fact]
    public async Task CompareAsync_SecondAttemptWithInventedHeaderIsRejectedWithoutThirdCall()
    {
        const string inquiry = "ＣＲＯＳヘッダについて確認してください。";
        var request = NonReadyRequest(inquiry) with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var client = new SequenceLlmClient(
            NonReadyResponse("確認できる事実 / 現時点で断定できない事項 / 追加で必要な確認"),
            NonReadyResponse("確認できる事実: CR-Oヘッダについて問い合わせを受けています。" +
                "現時点で断定できない事項: 仕様は確認できません。" +
                "追加で必要な確認: 原文と設定値を確認してください。"));

        var result = await new GroundedAnswerComparisonService(
            client, new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "NeedsReview" },
                shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.Equal(2, client.CallCount);
        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("技術値", StringComparison.Ordinal));
        Assert.Contains("ＣＲＯＳヘッダ", client.Prompts[1].UserPrompt);
    }

    [Fact]
    public async Task CompareAsync_RequiresInquiryVersionAndHotfixWithoutBaselineVersion()
    {
        const string inquiry = "CxSAST 9.7.4.1001 HF5でスキャンが処理待ち中です。";
        var request = NonReadyRequest(inquiry) with
        {
            Case = new CaseContext { ProductName = "Checkmarx" },
            InquiryFocus = new InquiryFocusExtractor().Extract(inquiry),
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var omitted = "確認できる事実: CxSASTのスキャンが処理待ち中と報告されています。" +
            "現時点で断定できない事項: 原因は確認できません。" +
            "追加で必要な確認: 実行時刻と関連ログを確認してください。";
        var retained = "確認できる事実: CxSAST 9.7.4.1001 HF5のスキャンが処理待ち中と報告されています。" +
            "現時点で断定できない事項: 原因は確認できません。" +
            "追加で必要な確認: 実行時刻と関連ログを確認してください。";
        var client = new SequenceLlmClient(NonReadyResponse(omitted), NonReadyResponse(retained));

        var result = await new GroundedAnswerComparisonService(client,
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(
            request, Baseline() with { CustomerReplyDraft = "確認中です。", Evidence = [], Readiness = "NeedsReview" },
            shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.Equal(2, client.CallCount);
        Assert.Contains("9.7.4.1001 HF5", client.Prompts[0].UserPrompt);
        Assert.Contains("問い合わせ対象版 9.7.4.1001 HF5", client.Prompts[1].UserPrompt);
        Assert.Equal(1, client.Prompts[1].UserPrompt.Split("Must Preserve Fact:").Length - 1);
        Assert.Contains("再生成必須Fact:", client.Prompts[1].UserPrompt);
        Assert.Contains("customerMeaning=問い合わせで明示された対象版は9.7.4.1001 HF5",
            client.Prompts[1].UserPrompt);
        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, result.Status);
    }

    [Fact]
    public async Task CompareAsync_RetryDirectlyRestatesOnlyMissingInquiryAndLogFacts()
    {
        const string inquiry = "CxSAST 9.7.4.1001 HF5でスキャンが処理待ち中です。";
        var request = NonReadyRequest(inquiry) with
        {
            Case = new CaseContext { ProductName = "Checkmarx" },
            InquiryFocus = new InquiryFocusExtractor().Extract(inquiry),
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsReview",
                ResolvedFacts =
                [
                    new ResolvedFact
                    {
                        Key = "CaseObservation", Status = "Candidate", SourceType = "CurrentCase",
                        EvidenceId = "case:history:1",
                        Value = "CxJobManager and CxSystemManager were stopped. After starting both services, the scan completed successfully.",
                    },
                    new ResolvedFact
                    {
                        Key = "ObservedLogFailure", Status = "Candidate", SourceType = "CurrentCase",
                        EvidenceId = "case:log:1",
                        Value = "添付ログにResultsReceiverのMQ接続失敗が記録されている。",
                    },
                ],
            },
        };
        const string recovery = "CxJobManagerとCxSystemManagerが停止し、両サービスの起動後にスキャン完了と案件履歴に記録されています。";
        const string ending = "現時点で断定できない事項: 根本原因は未確認です。" +
            "追加で必要な確認: CxSASTの設定値を確認してください。";
        var client = new SequenceLlmClient(
            NonReadyResponse("確認できる事実: " + recovery + ending),
            NonReadyResponse("確認できる事実: 顧客の対象版は9.7.4.1001 HF5です。" + recovery +
                "添付ログにResultsReceiverのMQ接続失敗が記録されています。" + ending));

        var result = await new GroundedAnswerComparisonService(client,
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(request,
            Baseline() with { CustomerReplyDraft = "確認中です。", Evidence = [], Readiness = "NeedsReview" },
            shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.Equal(2, client.CallCount);
        var retry = client.Prompts[1].UserPrompt.Split("一度目の回答は既存の検証で不合格です。", 2)[1];
        Assert.Contains("再生成必須Fact:", retry);
        Assert.Contains("添付ログにResultsReceiverのMQ接続失敗が記録されている。", retry);
        Assert.Contains("attribution=CurrentCase案件履歴・添付の観測", retry);
        Assert.Contains("9.7.4.1001 HF5", retry);
        Assert.Contains("本文へそのままコピーする原文技術値: [\"9.7.4.1001 HF5\"]", retry);
        Assert.Contains("attribution=顧客申告", retry);
        Assert.DoesNotContain("CxJobManager", retry);
        Assert.DoesNotContain("帰属付きFactの内容と未確認条件は一体", retry);
        Assert.DoesNotContain("既にお客様から提示された認識", retry);
        Assert.Equal(client.Prompts[0].OutputSchema?.GetRawText(), client.Prompts[1].OutputSchema?.GetRawText());
        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, result.Status);
        Assert.Equal("NeedsReview", result.Candidate?.Readiness);
    }

    [Fact]
    public async Task CompareAsync_RetryCopiesOriginalTechnicalSpellingWithoutUnicodeEscapes()
    {
        const string inquiry = "CxSASTのＣＲＯＳヘッダについて確認してください。";
        var request = NonReadyRequest(inquiry) with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        const string ending = "現時点で断定できない事項: 仕様は未確認です。" +
            "追加で必要な確認: CxSASTの設定値を確認してください。";
        var client = new SequenceLlmClient(
            NonReadyResponse("確認できる事実: CxSASTのCR-OPTIONSヘッダについてご申告があります。" + ending),
            NonReadyResponse("確認できる事実: CxSASTのＣＲＯＳヘッダについてご申告があります。" + ending));

        var result = await new GroundedAnswerComparisonService(client,
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(request,
            Baseline() with { Evidence = [], Readiness = "NeedsReview" },
            shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.Equal(2, client.CallCount);
        var retry = client.Prompts[1].UserPrompt.Split("一度目の回答は既存の検証で不合格です。", 2)[1];
        Assert.Contains("本文へそのままコピーする原文技術値: [\"ＣＲＯＳヘッダ\"]", retry);
        Assert.DoesNotContain("\\u", retry);
        Assert.DoesNotContain("CR-OPTIONS", retry);
        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, result.Status);
        Assert.Contains("ＣＲＯＳヘッダ", result.Candidate!.CustomerReplyDraft);
    }

    [Fact]
    public async Task CompareAsync_RetryRetainsUnverifiedManufacturerAttributionForKnownHistory()
    {
        const string inquiry = "CxSAST 9.7.2 ホットフィックス1でＣＲＯＳヘッダを付与していない認識です。誤検知でしょうか。";
        var request = NonReadyRequest(inquiry) with
        {
            InquiryFocus = new InquiryFocusExtractor().Extract(inquiry),
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "CaseObservation", Status = "Candidate", SourceType = "CurrentCase",
                    EvidenceId = "case:history:1",
                    Value = "メーカーより、バージョン9.7.7で不具合が修正されたとの回答がございました。",
                }],
            },
        };
        const string reply = "確認できる事実: CxSAST 9.7.2 ホットフィックス1でＣＲＯＳヘッダを付与していないとのご申告があります。" +
            "案件履歴には9.7.7で不具合が修正されたとの記録があります。" +
            "現時点で断定できない事項: 誤検知かどうかは未確認です。" +
            "追加で必要な確認: 原文と設定値を確認してください。";
        var client = new SequenceLlmClient(
            NonReadyResponse(reply.Replace("ＣＲＯＳヘッダ", "CR-Oヘッダ", StringComparison.Ordinal)),
            NonReadyResponse(reply.Replace("案件履歴には", "メーカー原文は未確認ですが、案件履歴には", StringComparison.Ordinal)));

        var result = await new GroundedAnswerComparisonService(client,
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(request,
            Baseline() with { Evidence = [], Readiness = "NeedsManufacturerConfirmation" },
            shadowReview: new ShadowReviewCriteria([], [], [], "NeedsManufacturerConfirmation"));

        Assert.Equal(2, client.CallCount);
        var retry = client.Prompts[1].UserPrompt.Split("一度目の回答は既存の検証で不合格です。", 2)[1];
        Assert.Contains("attribution=メーカー原文未確認のCurrentCase案件履歴", client.Prompts[1].UserPrompt);
        Assert.Contains("9.7.7で不具合修正とのメーカー回答があったという案件記録。メーカー原文は未確認", client.Prompts[1].UserPrompt);
        Assert.Contains("未提示情報として再質問しない", client.Prompts[1].UserPrompt);
        Assert.Contains("メーカー原文は未確認", result.GeneratedReplyDraft);
        Assert.Equal(client.Prompts[0].OutputSchema?.GetRawText(), client.Prompts[1].OutputSchema?.GetRawText());
    }

    [Fact]
    public async Task CompareAsync_RetryWithoutLostTechnicalIdentityDoesNotDuplicateHistory()
    {
        var request = NonReadyRequest("QACの設定変更後の再発について確認したいです。") with
        {
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsReview",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "ObservedLogFailure", Status = "Candidate", SourceType = "CurrentCase",
                    EvidenceId = "case:log:1", Value = "添付ログにMQ接続の失敗が記録されている。",
                }],
            },
        };
        const string ending = "現時点で断定できない事項: 今回の原因は未確認です。" +
            "追加で必要な確認: QACの設定変更履歴を教えていただけますか？";
        var client = new SequenceLlmClient(
            NonReadyResponse("確認できる事実: QACの設定変更後に再発したとのご申告があります。" + ending),
            NonReadyResponse("確認できる事実: 添付ログにMQ接続の失敗が記録されています。" + ending));

        var result = await new GroundedAnswerComparisonService(client,
            new SafetyRedactionService(), new FixedChecker()).CompareAsync(request,
            Baseline() with { Evidence = [], Readiness = "NeedsReview" },
            shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.Equal(2, client.CallCount);
        var retry = client.Prompts[1].UserPrompt.Split("一度目の回答は既存の検証で不合格です。", 2)[1];
        Assert.DoesNotContain("再生成必須Fact:", retry);
        Assert.Contains("検証失敗: 重要Factの欠落", retry);
        Assert.Contains("添付ログにMQ接続の失敗が記録されている。", client.Prompts[1].UserPrompt);
        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, result.Status);
    }

    [Fact]
    public async Task CompareAsync_AcceptsConcreteActionWithInquiryAnchorAndPreciseVerb()
    {
        const string inquiry = "Validateサーバーを起動できません。プロジェクトルートを確認したいです。";
        var request = NonReadyRequest(inquiry) with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var reply = "確認できる事実: Validateサーバーの起動失敗が報告されています。" +
            "現時点で断定できない事項: 設定が正しいかは確認できません。" +
            "追加で必要な確認: Validateサーバーのプロジェクトルートを明記してください。";

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(NonReadyResponse(reply)), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "NeedsReview" },
                shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, result.Status);
        Assert.Equal("NeedsReview", result.Candidate?.Readiness);
    }

    [Fact]
    public async Task CompareAsync_CompletesPriorOutcomeAttributionAndResolutionTogether()
    {
        var request = NonReadyRequest("QACの設定変更後の再発について確認したいです。") with
        {
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsReview",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "PriorCaseOutcome", Status = "Candidate", SourceType = "CurrentCase",
                    EvidenceId = "case:history:1",
                    Value = "ユーザーデータ領域変更後、約1週間再発しなかったため、当時は解決としてクローズした。",
                }],
            },
        };
        var reply = "確認できる事実: ユーザーデータ領域変更後、約1週間再発しなかった記録があります。" +
            "現時点で断定できない事項: 今回の原因は確認できません。" +
            "追加で必要な確認: 現在のQAC設定値を確認してください。";

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(NonReadyResponse(reply)), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "NeedsReview" },
                shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.Contains("案件履歴", result.GeneratedReplyDraft);
        Assert.Contains("解決としてクローズ", result.GeneratedReplyDraft);
        Assert.DoesNotContain(result.Reasons, reason => reason.Contains("重要Fact", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("QACの設定変更履歴と発生タイミングを教えていただけますか？")]
    [InlineData("QACのログ内容をご確認いただけますか？")]
    [InlineData("QACのログ内容を共有いただけますか？")]
    public async Task CompareAsync_AcceptsPoliteRequestForConcreteInquiryItem(string action)
    {
        var request = NonReadyRequest("QACの言語設定が変わります。再発原因を調査してください。") with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var reply = "確認できる事実: QACの言語設定が変わるとのご申告があります。" +
            "現時点で断定できない事項: 再発原因は未確認です。" +
            "追加で必要な確認: " + action;

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(NonReadyResponse(reply)), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "NeedsReview" },
                shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.Equal(GroundedComparisonStatuses.ReadyForHumanReview, result.Status);
        Assert.Equal("NeedsReview", result.Candidate?.Readiness);
    }

    [Theory]
    [InlineData("QACについて教えていただけますか？")]
    [InlineData("他製品のログ内容をご確認いただけますか？")]
    [InlineData("QACの設定変更履歴があります。その他を教えていただけますか？")]
    [InlineData("QACの設定変更履歴は参考情報です。")]
    public async Task CompareAsync_RejectsPoliteRequestWithoutConcreteInquiryItem(string action)
    {
        var request = NonReadyRequest("QACの言語設定が変わります。再発原因を調査してください。") with
        {
            FactResolution = new FactResolutionResult { AnswerReadiness = "NeedsReview" },
        };
        var reply = "確認できる事実: QACの言語設定が変わるとのご申告があります。" +
            "現時点で断定できない事項: 再発原因は未確認です。" +
            "追加で必要な確認: " + action;

        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(NonReadyResponse(reply)), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "NeedsReview" },
                shadowReview: new ShadowReviewCriteria([], [], [], "NeedsReview"));

        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("具体的な追加確認", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, true, "付与状況")]
    [InlineData(true, true, "付与状況")]
    [InlineData(true, false, "付与状況")]
    [InlineData(true, false, "実装状況")]
    public async Task CompareAsync_SupportsConditionalHistoryAndSeparatesKnownAbsenceFromMissingProvenance(bool historyInUnknown, bool includesSourceCondition, string repeatedKnownItem)
    {
        const string inquiry = "CxSAST 9.7.2 ホットフィックス1でＣＲＯＳヘッダを付与していない認識です。検出された指摘は誤検知でしょうか。";
        var request = NonReadyRequest(inquiry) with
        {
            Case = new CaseContext { ProductName = "Checkmarx" },
            InquiryFocus = new InquiryFocusExtractor().Extract(inquiry),
            Sources = [new SearchSource { SourceId = "past", SourceType = "PastCaseNote", ProductName = "Checkmarx", Text = "CxSAST バージョン9.7.2の言語サポート照会。", Score = .8 }],
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = [new ResolvedFact
                {
                    Key = "CaseObservation", Status = "Candidate", SourceType = "CurrentCase",
                    EvidenceId = "case:history:1", Value = "メーカーより、バージョン9.7.7で不具合が修正されたとの回答がございました。",
                }],
            },
        };
        var reply = historyInUnknown
            ? "確認できる事実: 顧客の対象版は9.7.2 ホットフィックス1です。" +
                (includesSourceCondition
                    ? "現時点で断定できない事項: バージョン9.7.7で修正との案件記録は、メーカー原文が未確認であるため製品仕様の確定事実とは言えません。"
                    : "現時点で断定できない事項: 9.7.7バージョンでの不具合修正に関するメーカー回答は案件履歴に記録されていますが、本件の検出内容との直接的な関連性は確認できません。") +
                $"追加で必要な確認: ＣＲＯＳヘッダの{repeatedKnownItem}を再確認いただけますか？"
            : "確認できる事実: 顧客の対象版は9.7.2 ホットフィックス1です。案件履歴にはバージョン9.7.7で不具合が修正されたとの記録があります。" +
                "現時点で断定できない事項: 誤検知かどうかはＣＲＯＳヘッダの有無を確認する必要があります。" +
                "追加で必要な確認: ＣＲＯＳヘッダが実際に付与されていないかを確認してください。";
        reply = reply.Replace("確認できる事実:", "確認できる事実: 誤検知の可能性をご相談いただいています。", StringComparison.Ordinal);
        var result = await new GroundedAnswerComparisonService(
            new RecordingLlmClient(NonReadyResponse(reply)), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { CustomerReplyDraft = "確認中です。", Readiness = "NeedsManufacturerConfirmation",
                Evidence = [new EvidenceItem { SourceId = "past", SourceType = "PastCaseNote" }] },
                shadowReview: new ShadowReviewCriteria(["Spring CORS疑義と修正記録、メーカー原文未確認"], ["正式保証をしない"], ["メーカー原文"], "NeedsManufacturerConfirmation"));
        Assert.True(result.Status == GroundedComparisonStatuses.ReadyForHumanReview, string.Join("; ", result.Reasons));
        Assert.Equal(0, result.CandidateQuality!.UnsupportedClaimCount);
        Assert.Equal("NeedsManufacturerConfirmation", result.Candidate!.Readiness);
        Assert.Contains("メーカー原文未確認の案件履歴", result.GeneratedReplyDraft);
        Assert.Contains("お客様からは「ＣＲＯＳヘッダを付与していない」との申告", result.GeneratedReplyDraft);
        Assert.Contains("メーカー回答原文", result.GeneratedReplyDraft);
        Assert.DoesNotContain("実際に付与されていないか", result.GeneratedReplyDraft);
        Assert.DoesNotContain("ヘッダの有無を確認", result.GeneratedReplyDraft);
        Assert.DoesNotContain("ヘッダの付与状況を再確認", result.GeneratedReplyDraft);
        Assert.DoesNotContain("ヘッダの実装状況を再確認", result.GeneratedReplyDraft);
        Assert.Matches("断定できません|確認できません", result.GeneratedReplyDraft);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompareAsync_RejectsCaseHistoryPromotedToGuarantee(bool inUnknownSection)
    {
        var request = NonReadyRequest("CxSASTの指摘は誤検知でしょうか。") with
        {
            FactResolution = new FactResolutionResult
            {
                AnswerReadiness = "NeedsManufacturerConfirmation",
                ResolvedFacts = [new ResolvedFact { Key = "CaseObservation", Status = "Candidate", SourceType = "CurrentCase",
                    EvidenceId = "case:history:1", Value = "メーカーより、バージョン9.7.7で不具合が修正されたとの回答がございました。" }],
            },
        };
        var reply = inUnknownSection
            ? "確認できる事実: 案件履歴にはバージョン9.7.7で修正との記録があります（メーカー原文未確認）。" +
                "現時点で断定できない事項: 案件履歴によりバージョン9.7.7で修正が正式保証されています。追加で必要な確認: メーカー回答原文を確認してください。"
            : "確認できる事実: 案件履歴によりバージョン9.7.7で修正が正式保証されています。" +
                "現時点で断定できない事項: 環境は未確認です。追加で必要な確認: メーカー回答原文を確認してください。";
        var result = await new GroundedAnswerComparisonService(new RecordingLlmClient(NonReadyResponse(reply)), new SafetyRedactionService(), new FixedChecker())
            .CompareAsync(request, Baseline() with { Evidence = [], Readiness = "NeedsManufacturerConfirmation" },
                shadowReview: new ShadowReviewCriteria([], [], [], "NeedsManufacturerConfirmation"));
        Assert.Equal(GroundedComparisonStatuses.Rejected, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("帰属の昇格", StringComparison.Ordinal));
    }

    private static AnswerDraftRequest NonReadyRequest(string inquiry) => Request() with
    {
        InquiryText = inquiry, Sources = [],
        Settings = new AiAssistantSettings { MaxPromptChars = 12000 },
    };

    private static string NonReadyResponse(string reply) => JsonSerializer.Serialize(new
    {
        customerReplyDraft = reply, internalMemo = "確認中",
        needConfirmations = new[] { new { question = "追加資料を確認してください。", reason = "未確認", priority = "High" } },
        evidence = Array.Empty<object>(), confidence = 0.2, warnings = Array.Empty<string>(),
    });

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

    private sealed class SequenceLlmClient(params string[] responses) : ILlmClient
    {
        public int CallCount { get; private set; }
        public List<PromptMessages> Prompts { get; } = [];

        public Task<LlmGenerationResult> GenerateAsync(PromptMessages messages,
            LlmProviderSettings settings, bool disableThinking = true,
            CancellationToken cancellationToken = default)
        {
            Prompts.Add(messages);
            var response = responses[Math.Min(CallCount++, responses.Length - 1)];
            return Task.FromResult(new LlmGenerationResult { Content = response });
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
