using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Inquiries;
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

    [Fact]
    public async Task GenerateDraftAsync_BuildsEvidenceBackedReplyWhenLlmRefusesDespiteSources()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "現時点の選択根拠からは、断定できる回答内容を確認できませんでした。",
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.2,
              "warnings": []
            }
            """);
        var request = CreateRequest(
            [
                new SearchSource
                {
                    SourceId = "qac-windows",
                    SourceType = "OfficialDoc",
                    Title = "Windows 11-64bit Revision 22H2 がサポートOSとして記載",
                    Text = "QACの対応OSとして Windows 11-64bit Revision 22H2 が記載されています。",
                    Score = 0.9,
                },
                new SearchSource
                {
                    SourceId = "qac-linux",
                    SourceType = "OfficialDoc",
                    Title = "Linux 用インストーラ (.sh/.run) が提供されています",
                    Text = "QACでは Linux 用インストーラ (.sh/.run) が提供されています。",
                    Score = 0.8,
                },
            ]) with
            {
                InquiryText = "QACの対応OSを教えてください",
            };

        var result = await service.GenerateDraftAsync(request);

        Assert.Contains("対応OS", result.CustomerReplyDraft);
        Assert.Contains("Windows 11-64bit", result.CustomerReplyDraft);
        Assert.Contains("Linux", result.CustomerReplyDraft);
        Assert.DoesNotContain("断定できる回答内容を確認できません", result.CustomerReplyDraft);
        Assert.Equal(2, result.Evidence.Count);
        Assert.True(result.Confidence >= 0.45);
        Assert.Contains(result.Warnings, warning => warning.Contains("送信済み根拠から回答案を補完", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDraftAsync_UsesSelectedPastCaseTechnicalContentWhenOfficialDocExistsAndLlmRefuses()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "現時点の参照根拠からは、断定できる回答内容を確認できませんでした。",
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [
                { "sourceId": "official-qac-12", "sourceType": "OfficialDoc", "title": "QAC 12.0 Release Notes", "excerpt": "QAC 12.0ではcxcast engine hotfix packの変更があります。", "relevance": 0.33 }
              ],
              "confidence": 0.2,
              "warnings": []
            }
            """);
        var request = CreateRequest(
            [
                new SearchSource
                {
                    SourceId = "official-qac-12",
                    SourceType = "OfficialDoc",
                    Title = "QAC 12.0 Release Notes",
                    Text = "QAC 12.0ではcxcast engine hotfix packの変更があります。",
                    Score = 0.33,
                },
                new SearchSource
                {
                    SourceId = "case-00015391",
                    SourceType = "PastCaseNote",
                    Title = "00015391 東洋電装株式会社 お客様ご相談",
                    Text = "*****追記部_2026/01/06 16:48:46(受付)***** 00015391 東洋電装株式会社 山田様 E-Mail : sample@example.test QAC 12.0ではcxcast engineのhotfix packを適用し、Validateの設定を更新することでコンパイラ認識を確認できました。",
                    SupportNumber = "00015391",
                    Score = 1.0,
                },
            ]) with
            {
                InquiryText = "QAC 12.0への変更に伴うコンパイラ認識について教えてください。",
                Settings = new AiAssistantSettings { MaxEvidenceItems = 8 },
            };

        var result = await service.GenerateDraftAsync(request);

        Assert.Contains("確認できた内容", result.CustomerReplyDraft);
        Assert.Contains("QAC 12.0", result.CustomerReplyDraft);
        Assert.Contains("Validate", result.CustomerReplyDraft);
        Assert.Contains("hotfix pack", result.CustomerReplyDraft);
        Assert.DoesNotContain("断定できる回答内容を確認できません", result.CustomerReplyDraft);
        Assert.DoesNotContain("00015391", result.CustomerReplyDraft);
        Assert.DoesNotContain("東洋電装", result.CustomerReplyDraft);
        Assert.DoesNotContain("山田", result.CustomerReplyDraft);
        Assert.DoesNotContain("sample@example.test", result.CustomerReplyDraft);
        Assert.DoesNotContain("追記部", result.CustomerReplyDraft);
        Assert.Contains(result.Evidence, item => item.SourceId == "official-qac-12");
        Assert.Contains(result.Evidence, item => item.SourceId == "case-00015391");
    }

    [Fact]
    public async Task GenerateDraftAsync_UsesClosedPastCaseActionContentAndAddsCurrentRecipientHeader()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "別件としてライセンス設定を確認してください。",
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.4,
              "warnings": []
            }
            """);
        var request = CreateRequest(
            [
                new SearchSource
                {
                    SourceId = "closed-case-installer",
                    SourceType = "PastCaseNote",
                    Title = "00018456 東陽ユーティリティ株式会社 お客様への返信案",
                    Text = "クローズ済み。お客様への返信案: 00018456 東陽ユーティリティ株式会社 鈴木様 E-Mail: old@example.test 確認結果として、TOYO_UTIL_PY3.zip をアップロードし、Validate利用手順書.pdf と RepriseSettingGuide_Linux.pdf を送付対象として案内しました。",
                    SupportNumber = "00018456",
                    Score = 1.0,
                },
            ]) with
            {
                Case = new CaseContext
                {
                    CompanyName = "Corp",
                    CustomerName = "佐藤 太郎",
                    SupportNumber = "SUP-100",
                },
                InquiryText = "QAC 2025.4向けに送付するファイルを確認したいです。",
                Settings = new AiAssistantSettings { MaxEvidenceItems = 8 },
            };

        var result = await service.GenerateDraftAsync(request);

        Assert.StartsWith($"Corp{Environment.NewLine}佐藤 太郎 様", result.CustomerReplyDraft);
        Assert.Contains("確認できた対応内容", result.CustomerReplyDraft);
        Assert.Contains("TOYO_UTIL_PY3.zip", result.CustomerReplyDraft);
        Assert.Contains("Validate利用手順書.pdf", result.CustomerReplyDraft);
        Assert.Contains("RepriseSettingGuide_Linux.pdf", result.CustomerReplyDraft);
        Assert.DoesNotContain("別件としてライセンス設定", result.CustomerReplyDraft);
        Assert.DoesNotContain("00018456", result.CustomerReplyDraft);
        Assert.DoesNotContain("東陽ユーティリティ", result.CustomerReplyDraft);
        Assert.DoesNotContain("鈴木", result.CustomerReplyDraft);
        Assert.DoesNotContain("old@example.test", result.CustomerReplyDraft);
    }

    [Fact]
    public async Task GenerateDraftAsync_DoesNotUseIrrelevantManualsForDashboardInquiryWithYoshiharaSignature()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "現時点の参照根拠からは、断定できる回答内容を確認できませんでした。",
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.2,
              "warnings": []
            }
            """);
        var inquiry = """
            東陽テクニカ　テクニカルサポートご担当者様

            Astemo株式会社の吉原です。

            今回は、Dashboard利用手順書を提供していただけないかお願いしたく、ご連絡いたしました。
            具体的な利用方法や設定手順、トラブルシューティングの情報などが含まれている手順書をご提供いただけますと幸いです。

            吉原　裕人 | Yuto Yoshihara
            """;
        var request = CreateRequest(
            [
                new SearchSource
                {
                    SourceId = "mc25cm",
                    SourceType = "Manual",
                    Title = "MC25CM Component Manual",
                    Text = "MC25CMコンポーネントマニュアル 重要な注意事項 会社の所有権の変更について Programming Research Ltd. は Perforce Software Inc. の完全子会社となりました。",
                    Score = 0.9,
                },
                new SearchSource
                {
                    SourceId = "ascm",
                    SourceType = "Manual",
                    Title = "ASCM Component Manual",
                    Text = "ASCMコンポーネントマニュアル 重要な注意事項 会社の所有権の変更について Programming Research Ltd. は Perforce Software Inc. の完全子会社となりました。",
                    Score = 0.86,
                },
            ]) with
            {
                Case = new CaseContext
                {
                    CompanyName = "Astemo株式会社",
                    CustomerName = "吉原 裕人",
                    SupportNumber = "SUP-200",
                },
                InquiryText = inquiry,
                InquiryFocus = new InquiryFocusExtractor().Extract(inquiry),
                Settings = new AiAssistantSettings { MaxEvidenceItems = 8 },
            };

        var result = await service.GenerateDraftAsync(request);

        Assert.StartsWith($"Astemo株式会社{Environment.NewLine}吉原 裕人 様", result.CustomerReplyDraft);
        Assert.Contains("Dashboard", result.CustomerReplyDraft);
        Assert.Contains("直接該当する回答根拠を確認できません", result.CustomerReplyDraft);
        Assert.DoesNotContain("対応OS", result.CustomerReplyDraft);
        Assert.DoesNotContain("MC25CM", result.CustomerReplyDraft);
        Assert.DoesNotContain("ASCM", result.CustomerReplyDraft);
        Assert.DoesNotContain("Programming Research", result.CustomerReplyDraft);
    }

    [Fact]
    public async Task GenerateDraftAsync_RedactsPastCaseCustomerLeakFromCustomerReply()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "お問い合わせいただいた対応OSについて、以下を確認できます。\n・00014623 東海理化 お客様ご相談内容 追記部 いつもお世話になっております。東陽テクニカ 技術サポート担当です。\n上記以外は追加確認が必要です。",
              "internalMemo": "source-1 referenced.",
              "needConfirmations": [],
              "evidence": [
                { "sourceId": "source-1", "sourceType": "PastCaseNote", "title": "過去案件", "excerpt": "抜粋", "relevance": 0.8 }
              ],
              "confidence": 0.7,
              "warnings": []
            }
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.DoesNotContain("00014623", result.CustomerReplyDraft);
        Assert.DoesNotContain("東海理化", result.CustomerReplyDraft);
        Assert.DoesNotContain("東陽テクニカ", result.CustomerReplyDraft);
        Assert.DoesNotContain("追記部", result.CustomerReplyDraft);
        Assert.DoesNotContain("技術サポート担当", result.CustomerReplyDraft);
        Assert.Contains("お客様向け回答案から過去案件由来", string.Join(Environment.NewLine, result.Warnings));
    }

    [Fact]
    public async Task GenerateDraftAsync_DoesNotExposePastCaseDetailsInEvidenceBackedFallback()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "現時点の選択根拠からは、断定できる回答内容を確認できませんでした。",
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.2,
              "warnings": []
            }
            """);
        var request = CreateRequest(
            [
                new SearchSource
                {
                    SourceId = "case-qac-os",
                    SourceType = "PastCaseNote",
                    Title = "00014623 東海理化 お客様ご相談内容",
                    Text = "東海理化 七尾様。Perforce QACのサポートOSについて質問があります。Windows 11-64bit Revision 22H2 がサポートOSとして記載されています。",
                    Score = 0.9,
                },
            ]) with
            {
                InquiryText = "QACの対応OSを教えてください",
            };

        var result = await service.GenerateDraftAsync(request);

        Assert.Contains("過去案件情報が中心", result.CustomerReplyDraft);
        Assert.Contains("転記できません", result.CustomerReplyDraft);
        Assert.DoesNotContain("00014623", result.CustomerReplyDraft);
        Assert.DoesNotContain("東海理化", result.CustomerReplyDraft);
        Assert.DoesNotContain("七尾", result.CustomerReplyDraft);
        Assert.DoesNotContain("Windows 11-64bit", result.CustomerReplyDraft);
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
