using System.Text.Json;
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

        Assert.EndsWith("Please check settings.", result.CustomerReplyDraft);
        Assert.Equal("source-1 referenced.", result.InternalMemo);
        Assert.Single(result.NeedConfirmations);
        Assert.Single(result.Evidence);
        Assert.Equal(0.7, result.Confidence);
        Assert.Null(result.AnswerQuality);
    }

    [Fact]
    public async Task GenerateDraftAsync_AnswerQualityGateOff_PreservesLegacyResult()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "Please check settings.",
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.7,
              "warnings": []
            }
            """);

        var result = await service.GenerateDraftAsync(CreateRequest());

        Assert.Null(result.AnswerQuality);
        Assert.DoesNotContain(
            result.Warnings,
            warning => warning.Contains("Answer Quality", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDraftAsync_AnswerQualityGateOn_AddsDiagnosticsWithoutChangingDraft()
    {
        const string answer = "qacli validate build --project Demo を実行し、Validate portalで結果を確認してください。";
        var service = CreateService($$"""
            {
              "customerReplyDraft": {{System.Text.Json.JsonSerializer.Serialize(answer)}},
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.7,
              "warnings": []
            }
            """);
        var request = CreateRequest(
        [
            new SearchSource
            {
                SourceId = "manual-1",
                SourceType = "Manual",
                Title = "Upload manual",
                Text = answer,
                ProductName = "HelixQAC",
                Score = 0.9,
            },
        ]) with
        {
            Case = new CaseContext { ProductName = "HelixQAC" },
            InquiryText = "Validateへ解析結果をアップロードするコマンドと確認方法を教えてください。",
            Settings = new AiAssistantSettings { UseAnswerQualityGate = true },
        };

        var result = await service.GenerateDraftAsync(request);

        Assert.EndsWith(answer, result.CustomerReplyDraft);
        Assert.NotNull(result.AnswerQuality);
        Assert.Contains(
            result.Warnings,
            warning => warning.StartsWith("Answer Quality Gate:", StringComparison.Ordinal));
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
    public async Task GenerateDraftAsync_RecoversValidateGuiAndCliProcedureWhenJsonIsTruncated()
    {
        var service = CreateService("""
            { "customerReplyDraft": "回答を作成中です
            """);
        var request = CreateRequest(
            [
                new SearchSource
                {
                    SourceId = "validate-upload-procedure",
                    SourceType = "Manual",
                    Title = "Perforce_QAC_Manual",
                    Text = "QA·GUIからValidateに解析結果をアップロードするには以下のメニューを使用します。［ポータル］>［Validate］>［解析結果をアップロード］。QA·CLIではqacli validate build --qaf-project . を実行します。アップロードにはValidateでの認証、適切な権限、ビルドライセンスが必要です。",
                    Score = 0.92,
                },
            ]) with
            {
                InquiryText = "QACで解析した結果をValidateへアップロードする方法を教えて。GUIでのアップロード方法及びCLIでの方法についても教えて。",
                InquiryFocus = new InquiryFocusExtractor().Extract("QACで解析した結果をValidateへアップロードする方法を教えて。GUIでのアップロード方法及びCLIでの方法についても教えて。"),
                Settings = new AiAssistantSettings { MaxEvidenceItems = 3 },
            };

        var result = await service.GenerateDraftAsync(request);

        Assert.Contains("［ポータル］>［Validate］>［解析結果をアップロード］", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("qacli validate build --qaf-project .", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ビルドライセンス", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("LLM応答を解析できませんでした", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, warning => warning.Contains("JSON解析に失敗", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("Validateアップロード手順を補完", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDraftAsync_RecoversValidateStreamOverviewAndConfigurationWhenJsonIsTruncated()
    {
        var service = CreateService("""
            { "customerReplyDraft": "回答を作成中です
            """);
        var request = CreateRequest(
            [
                new SearchSource
                {
                    SourceId = "official-stream-cli",
                    SourceType = "OfficialDoc",
                    Title = "qacli validate build",
                    Text = "qacli validate config --create -P <project_dir> --url <validate_url> --validate-project <validate_project_name> を使用します。--validate-projectは、Validateに保存されているPerforce QACプロジェクトの生成時に使用するValidateプロジェクト/ストリーム名です。",
                    Score = 0.96,
                },
                new SearchSource
                {
                    SourceId = "manual-stream-overview",
                    SourceType = "Manual",
                    Title = "Perforce-QAC-Manual",
                    Text = "ストリームのビルドをトラッキングします。これは、開発者がプロジェクトのローカルコピーで開発をしている間に起きた可能性のある新しい問題点に集中することを可能にします。プロジェクトの異なるバージョンをトラッキングし、Perforce QACプロジェクトを特定のストリームに接合するために、Validate内でストリームを生成できます。qacli validate connectでプロジェクト間の接続を作成します。",
                    Score = 0.92,
                },
                new SearchSource
                {
                    SourceId = "past-stream-case",
                    SourceType = "PastCaseNote",
                    Title = "類似案件",
                    Text = "Validateのストリーム設定を確認した過去案件です。",
                    Score = 0.73,
                },
            ]) with
            {
                InquiryText = "Validateのストリーム機能についてどのような機能かを教えてください。また、設定方法について教えてください。",
                InquiryFocus = new InquiryFocusExtractor().Extract("Validateのストリーム機能についてどのような機能かを教えてください。また、設定方法について教えてください。"),
                Settings = new AiAssistantSettings
                {
                    MaxEvidenceItems = 3,
                    UseCoverageAwareEvidenceSelection = true,
                },
            };

        var result = await service.GenerateDraftAsync(request);

        Assert.Contains("【概要】", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("【設定方法】", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("【注意点】", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("新しい問題", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("--validate-project", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("確認できた内容", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("Perforce-QAC-Manual（", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, warning => warning.Contains("Validate Streamの概要と設定方法を補完", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDraftAsync_RecoversQacAnalysisProcedureWhenJsonIsTruncated()
    {
        var service = CreateService("""
            { "customerReplyDraft": "TOYO\nご担当者様\n回答を作成中です
            """);
        var question = "QACで、プロジェクトを解析するための手順を教えてください。";
        var request = CreateRequest(
            [
                new SearchSource
                {
                    SourceId = "analysis-official",
                    SourceType = "OfficialDoc",
                    Title = "Analyze a project",
                    Text = "Run qacli analyze -P <project-directory> to analyze the QAC project, then check the analysis result.",
                    Score = 0.95,
                },
                new SearchSource
                {
                    SourceId = "analysis-manual",
                    SourceType = "Manual",
                    Title = "Perforce-QAC-Manual",
                    Text = "QACプロジェクトを解析する前にソース、インクルードパス、マクロ定義、コンパイラ設定を確認し、解析を実行します。",
                    Score = 0.90,
                },
            ]) with
            {
                Case = new CaseContext { ProductName = "HelixQAC" },
                InquiryText = question,
                InquiryFocus = new InquiryFocusExtractor().Extract(question),
                Settings = new AiAssistantSettings { MaxEvidenceItems = 3 },
            };

        var result = await service.GenerateDraftAsync(request);

        Assert.StartsWith($"[会社名]{Environment.NewLine}[お客様名] 様", result.CustomerReplyDraft);
        Assert.Contains("【事前準備】", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("【GUIでの手順】", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("【CLIでの手順】", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("【解析結果の確認】", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("qacli analyze -P <project-directory>", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("【注意点】", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("【参照先】", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("TOYO", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Warnings, warning => warning.Contains("HowTo回答を選択済み根拠に基づく操作順へ補正", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"customerReplyDraft\":")]
    public async Task GenerateDraftAsync_RecoversQacAnalysisProcedureForEmptyOrMalformedResponses(string response)
    {
        var service = CreateService(response);
        var request = CreateAnalysisHowToRequest();

        var result = await service.GenerateDraftAsync(request);

        AssertAnalysisHowToStructure(result.CustomerReplyDraft);
        Assert.Contains("qacli analyze -P <project-directory>", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOYO", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFailureFallback_UsesSameAnalysisHowToStructureForTimeout()
    {
        var result = AnswerPostProcessor.BuildFailureFallback(
            CreateAnalysisHowToRequest(),
            new TimeoutException("simulated timeout"));

        AssertAnalysisHowToStructure(result.CustomerReplyDraft);
        Assert.Contains("・『Perforce-QAC-Manual』", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("Page 24", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("「プロジェクトの解析」項", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("qacli analyze -P <project-directory>", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PDFマニュアルの該当根拠", result.CustomerReplyDraft, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFailureFallback_DoesNotExposePastCaseReferenceToCustomer()
    {
        var request = CreateAnalysisHowToRequest() with
        {
            Sources =
            [
                .. CreateAnalysisHowToRequest().Sources,
                new SearchSource
                {
                    SourceId = "past-1",
                    SourceType = "PastCaseNote",
                    Title = "過去案件 00012345 顧客名",
                    DocumentTitle = "過去案件 00012345 顧客名",
                    Text = "QACプロジェクトの解析を実行しました。",
                    Score = 0.8,
                },
            ],
        };

        var result = AnswerPostProcessor.BuildFailureFallback(request, new TimeoutException("simulated"));

        Assert.DoesNotContain("00012345", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("過去案件", result.CustomerReplyDraft, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(12, "", "Page 12", "「")]
    [InlineData(0, "解析の実行", "「解析の実行」項", "Page ")]
    [InlineData(0, "", "・『Perforce-QAC-Manual』", "Page ")]
    public void BuildFailureFallback_ReferenceUsesOnlyAvailableMetadata(
        int pageNumber,
        string sectionTitle,
        string expected,
        string forbidden)
    {
        var source = CreateAnalysisHowToRequest().Sources[0] with
        {
            PageNumber = pageNumber == 0 ? null : pageNumber,
            SectionTitle = sectionTitle,
        };
        var request = CreateAnalysisHowToRequest() with { Sources = [source] };

        var result = AnswerPostProcessor.BuildFailureFallback(
            request,
            new TimeoutException("simulated"));

        Assert.Contains(expected, result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain(forbidden, result.CustomerReplyDraft, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFailureFallback_UsesQaguiProjectAnalysisAndDialogProgressFromEvidence()
    {
        var request = CreateAnalysisHowToRequest() with
        {
            Sources =
            [
                new SearchSource
                {
                    SourceId = "analysis-live-manual",
                    SourceType = "Manual",
                    Title = "Perforce-QAC-Manual",
                    DocumentTitle = "Perforce-QAC-Manual",
                    Text = "QAGUI以下の手順を実行します。[解析(N)]>プロジェクト全体のファイルベース解析:解析ダイアログボックス。解析中ダイアログボックスにプロセスが表示されます。QA CLIではqaclianalyze-cf-P<directory>を実行します。",
                    Score = 0.95,
                },
            ],
        };

        var result = AnswerPostProcessor.BuildFailureFallback(
            request,
            new TimeoutException("simulated timeout"));

        Assert.Contains("[解析(N)]>プロジェクト全体のファイルベース解析", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("解析中ダイアログにプロセスが表示", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("qacli analyze -cf -P<directory>", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFailureFallback_DoesNotAppendPdfProseToAnalysisCommand()
    {
        var request = CreateAnalysisHowToRequest() with
        {
            Sources =
            [
                new SearchSource
                {
                    SourceId = "analysis-pdf-command",
                    SourceType = "Manual",
                    Title = "Perforce-QAC-Manual",
                    Text = "qaclianalyze-P<directory>-C<cma-project-name>-csgaこのコマンドは、関連付けられたモジュールを消去します。",
                    Score = 0.95,
                },
            ],
        };

        var result = AnswerPostProcessor.BuildFailureFallback(
            request,
            new TimeoutException("simulated timeout"));

        Assert.DoesNotContain("qacli analyze -P<directory>", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-C<cma-project-name> -csga", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<cma-project-name>-csga", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("このコマンドは", result.CustomerReplyDraft, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFailureFallback_SeparatesLongAnalysisOptionsJoinedByPdfExtraction()
    {
        var request = CreateAnalysisHowToRequest() with
        {
            Sources =
            [
                new SearchSource
                {
                    SourceId = "analysis-pdf-long-options",
                    SourceType = "Manual",
                    Title = "Perforce-QAC-Manual",
                    Text = "qaclianalyze-P<directory>--raw-source<file-path>--language-cct<cct-path>",
                    Score = 0.95,
                },
            ],
        };

        var result = AnswerPostProcessor.BuildFailureFallback(
            request,
            new TimeoutException("simulated timeout"));

        Assert.DoesNotContain("qacli analyze", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildFailureFallback_FileDeliveryAccessUsesPastCaseChecksAndAlternatives()
    {
        const string inquiry = """
            QAC 2026.2の最新版をFibeからダウンロードできません。
            一つ前の2026.1を入手したいため、別の提供方法を教えてください。
            """;
        var request = new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = "HelixQAC" },
            InquiryText = inquiry,
            InquiryFocus = new InquiryFocusExtractor().Extract(inquiry),
            Sources =
            [
                new SearchSource
                {
                    SourceId = "past-answer-fiebie",
                    SourceType = "ExactPastAnswer",
                    Title = "類似案件の回答",
                    Text = "FiebieでOTP認証後、/api/file/download/contentからダウンロードできません。Webフィルタ、プロキシ、SSL検査、ブラウザ、ドメイン許可、別ネットワークを確認しました。",
                    Score = 0.9,
                },
            ],
            Settings = new AiAssistantSettings { MaxEvidenceItems = 3 },
        };

        var result = AnswerPostProcessor.BuildFailureFallback(request, new TimeoutException("simulated"));

        Assert.StartsWith($"[会社名]{Environment.NewLine}[お客様名] 様", result.CustomerReplyDraft);
        Assert.Contains("ファイル転送サービス「Fiebie」", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("Webフィルタ", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("プロキシ／SSL検査", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("EdgeまたはChrome", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("SharePoint／OneDrive", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("メール添付", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("具体的な解消方法までは記録されていません", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("TOYO", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("00016722", result.CustomerReplyDraft, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateDraftAsync_FileDeliveryAccessReplacesGenericSuccessfulReplyWithPastCaseGuidance()
    {
        const string inquiry = "QAC 2026.2をFibeからダウンロードできません。2026.1を別の方法で提供できますか。";
        var service = CreateService("""
            {
              "customerReplyDraft": "ダウンロードサイト以外の提供方法が確定していないため、社内確認します。",
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.7,
              "warnings": []
            }
            """);
        var request = new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = "HelixQAC" },
            InquiryText = inquiry,
            InquiryFocus = new InquiryFocusExtractor().Extract(inquiry),
            Sources =
            [
                new SearchSource
                {
                    SourceId = "past-answer-fiebie",
                    SourceType = "PastAnswer",
                    Title = "類似案件の回答",
                    Text = "Fiebieから取得できないため、Webフィルタ、プロキシ、SSL検査、ドメイン許可と別ネットワークを確認しました。",
                    Score = 0.8,
                },
            ],
            Settings = new AiAssistantSettings { MaxEvidenceItems = 3 },
        };

        var result = await service.GenerateDraftAsync(request);

        Assert.Contains("ファイル転送サービス「Fiebie」", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("SharePoint／OneDrive", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains("具体的な解消方法までは記録されていません", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, warning => warning.Contains("類似案件", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDraftAsync_PreservesStructuredAnalysisHowToFromSuccessfulLlm()
    {
        var response = JsonSerializer.Serialize(new
        {
            customerReplyDraft = "[会社名]\n[お客様名] 様\n\n【事前準備】\nソースとコンパイラ設定を確認します。\n\n【GUIでの手順】\nQAC GUIで解析を実行します。\n\n【CLIでの手順】\nqacli analyze -P <project-directory> を実行します。\n\n【解析結果の確認】\n解析結果を確認します。\n\n【注意点】\n対象バージョンを確認してください。\n\n【参照先】\nPerforce-QAC-Manual",
            internalMemo = "",
            needConfirmations = Array.Empty<string>(),
            evidence = Array.Empty<object>(),
            confidence = 0.9,
            warnings = Array.Empty<string>(),
        });
        var result = await CreateService(response).GenerateDraftAsync(CreateAnalysisHowToRequest());

        AssertAnalysisHowToStructure(result.CustomerReplyDraft);
        Assert.Contains("QAC GUIで解析を実行", result.CustomerReplyDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("TOYO", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
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
    public async Task GenerateDraftAsync_MissingRecipientUsesPlaceholdersAndNeverInfersToyo()
    {
        var service = CreateService("""
            {
              "customerReplyDraft": "お問い合わせいただきありがとうございます。",
              "internalMemo": "",
              "needConfirmations": [],
              "evidence": [],
              "confidence": 0.7,
              "warnings": []
            }
            """);
        var request = CreateRequest() with
        {
            Case = new CaseContext
            {
                CompanyName = "TOYO",
                CustomerName = "",
                ProductName = "HelixQAC",
            },
        };

        var result = await service.GenerateDraftAsync(request);

        Assert.StartsWith($"[会社名]{Environment.NewLine}[お客様名] 様", result.CustomerReplyDraft);
        Assert.DoesNotContain("TOYO", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ご担当者様", result.CustomerReplyDraft, StringComparison.Ordinal);
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

    private static AnswerDraftRequest CreateAnalysisHowToRequest()
    {
        const string question = "QACで、プロジェクトを解析するための手順を教えてください。";
        return new AnswerDraftRequest
        {
            Case = new CaseContext { ProductName = "HelixQAC" },
            InquiryText = question,
            InquiryFocus = new InquiryFocusExtractor().Extract(question),
            Sources =
            [
                new SearchSource
                {
                    SourceId = "analysis-manual",
                    SourceType = "Manual",
                    Title = "Perforce-QAC-Manual",
                    DocumentTitle = "Perforce-QAC-Manual",
                    PageNumber = 24,
                    SectionTitle = "プロジェクトの解析",
                    Text = "解析前にソースファイル、コンパイラ設定、インクルードパス、マクロ定義を確認します。QAC GUIの［解析］メニューで［解析を実行］を選択します。qacli analyze -P <project-directory> を実行します。解析終了後に解析結果を確認します。",
                    Score = 0.95,
                },
            ],
            Settings = new AiAssistantSettings { MaxEvidenceItems = 3 },
        };
    }

    private static void AssertAnalysisHowToStructure(string reply)
    {
        Assert.StartsWith($"[会社名]{Environment.NewLine}[お客様名] 様", reply);
        Assert.Contains("【事前準備】", reply, StringComparison.Ordinal);
        Assert.Contains("【GUIでの手順】", reply, StringComparison.Ordinal);
        Assert.Contains("【CLIでの手順】", reply, StringComparison.Ordinal);
        Assert.Contains("【解析結果の確認】", reply, StringComparison.Ordinal);
        Assert.Contains("【注意点】", reply, StringComparison.Ordinal);
        Assert.Contains("【参照先】", reply, StringComparison.Ordinal);
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
