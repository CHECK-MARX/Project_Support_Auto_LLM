using System.Text;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Artifacts;
using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.AiAssistant.App.ViewModels;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class CodexChatViewModelTests
{
    [Fact]
    public void FinalReviewCommand_IsDisabledUntilCurrentCaseThreadExists()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(temp, new FakeClient());

        viewModel.TechnicalAnswer = "技術回答案";

        Assert.False(viewModel.FinalReviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConnectCommand_WhenIdle_DoesNotShowFalseProgress()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        var viewModel = CreateViewModel(temp, fakeClient);

        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConnectionState == CodexConnectionState.Connected, TimeSpan.FromSeconds(5));

        Assert.Equal(0, viewModel.ProgressPercent);
        Assert.Equal("接続済み・調査待ち", viewModel.ProgressText);
    }

    [Fact]
    public async Task NewThread_UsesPersistedModelAndReasoningAndDisplaysActualValues()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        var savedModel = string.Empty;
        var savedReasoning = string.Empty;
        var viewModel = new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "SyntheticProduct",
                SupportId = "SYN-CODEX-SETTINGS",
                CaseFolder = temp.Path,
                InquiryText = "設定確認",
            },
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { },
            codexSelectionProvider: () => (savedModel, savedReasoning),
            codexSelectionUpdated: (model, reasoning) =>
            {
                savedModel = model ?? string.Empty;
                savedReasoning = reasoning ?? string.Empty;
            });

        savedModel = "fake";
        savedReasoning = "medium";
        await viewModel.InitializeAsync();
        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConnectionState == CodexConnectionState.Connected, TimeSpan.FromSeconds(5));

        Assert.Equal("fake", viewModel.SelectedModel);
        Assert.Equal("medium", viewModel.SelectedReasoningEffort);
        Assert.Contains(viewModel.AvailableReasoningEfforts, item => item.Value == "medium");

        viewModel.PromptInput = "設定された値で調査してください";
        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer == "回答です。", TimeSpan.FromSeconds(5));

        Assert.Equal("fake", fakeClient.LastRequestedModel);
        Assert.Equal("medium", fakeClient.LastRequestedReasoningEffort);
        Assert.Equal("fake", viewModel.ActualModel);
        Assert.Equal("medium", viewModel.ActualReasoningEffort);
        Assert.Contains("fake / medium", viewModel.ActualModelAndReasoning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_UsesRuntimeDiscoveredLunaAsRecommendedModel()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient
        {
            Models =
            [
                new CodexModelInfo(
                    "gpt-5.6-sol",
                    "GPT-5.6-Sol",
                    true,
                    false,
                    "medium",
                    [new CodexReasoningEffortInfo("medium", "通常利用向け")]),
                new CodexModelInfo(
                    "gpt-5.6-luna",
                    "GPT-5.6-Luna",
                    false,
                    false,
                    "medium",
                    [new CodexReasoningEffortInfo("medium", "通常利用向け")]),
            ],
        };
        var viewModel = CreateViewModel(temp, fakeClient);

        await viewModel.InitializeAsync();
        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConnectionState == CodexConnectionState.Connected, TimeSpan.FromSeconds(5));

        Assert.Equal("gpt-5.6-luna", viewModel.SelectedModel);
        Assert.Equal("medium", viewModel.SelectedReasoningEffort);
    }

    [Fact]
    public async Task Connect_PreservesSavedModelAndReasoningWhenRuntimeListsDifferentModel()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient
        {
            Models =
            [
                new CodexModelInfo(
                    "gpt-6-astra",
                    "GPT-6 Astra",
                    false,
                    false,
                    "ultra",
                    [new CodexReasoningEffortInfo("ultra", "Maximum")]),
                new CodexModelInfo(
                    "gpt-5.6-luna",
                    "GPT-5.6 Luna",
                    true,
                    false,
                    "medium",
                    [new CodexReasoningEffortInfo("medium", "Normal")]),
            ],
        };
        var viewModel = CreateViewModelWithSelection(temp, fakeClient, "gpt-6-astra", "ultra");

        await viewModel.InitializeAsync();
        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConnectionState == CodexConnectionState.Connected, TimeSpan.FromSeconds(5));

        Assert.Equal("gpt-6-astra", viewModel.SelectedModel);
        Assert.Equal("ultra", viewModel.SelectedReasoningEffort);
        Assert.Equal("gpt-6-astra", viewModel.Model);
        Assert.Contains("次の新しい調査", viewModel.CodexSelectionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_PreservesUnavailableSavedModelAndReportsExplicitState()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient
        {
            Models =
            [new CodexModelInfo(
                "gpt-5.6-luna",
                "GPT-5.6 Luna",
                true,
                false,
                "medium",
                [new CodexReasoningEffortInfo("medium", "Normal")])],
        };
        var viewModel = CreateViewModelWithSelection(temp, fakeClient, "gpt-6-astra", "ultra");

        await viewModel.InitializeAsync();
        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConnectionState == CodexConnectionState.Connected, TimeSpan.FromSeconds(5));

        Assert.Equal("gpt-6-astra", viewModel.SelectedModel);
        Assert.Equal("ultra", viewModel.SelectedReasoningEffort);
        Assert.Contains("Requested Model", viewModel.CodexSelectionStatus, StringComparison.Ordinal);
        Assert.Contains("利用できません", viewModel.CodexSelectionStatus, StringComparison.Ordinal);
        Assert.Contains("利用できません", viewModel.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaseOverride_UsesRuntimeSelectionForNextNewThread_WithoutChangingGlobalSelection()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient
        {
            Models =
            [
                new CodexModelInfo(
                    "gpt-6-astra",
                    "GPT-6 Astra",
                    true,
                    false,
                    "xhigh",
                    [new CodexReasoningEffortInfo("xhigh", "高度")]),
                new CodexModelInfo(
                    "gpt-5.6-luna",
                    "GPT-5.6 Luna",
                    false,
                    false,
                    "medium",
                    [new CodexReasoningEffortInfo("medium", "標準")]),
            ],
        };
        string? caseModel = null;
        string? caseReasoning = null;
        var viewModel = new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "SyntheticProduct",
                SupportId = "SYN-CASE-OVERRIDE",
                CaseFolder = temp.Path,
                InquiryText = "案件別設定確認",
            },
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { },
            codexSelectionProvider: () => ("gpt-6-astra", "xhigh"),
            caseCodexSelectionProvider: () => (caseModel, caseReasoning),
            caseCodexSelectionUpdated: (model, reasoning) =>
            {
                caseModel = model;
                caseReasoning = reasoning;
            });

        await viewModel.InitializeAsync();
        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConnectionState == CodexConnectionState.Connected, TimeSpan.FromSeconds(5));

        Assert.Equal("全体設定を使用", viewModel.AvailableCaseModels[0].DisplayName);
        Assert.Equal("Astra / 超高", viewModel.EffectiveSettingsDisplay);
        Assert.Equal("gpt-6-astra", viewModel.EffectiveCodexModel);
        Assert.Equal("xhigh", viewModel.EffectiveCodexReasoning);
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        Assert.Equal("", viewModel.CaseSelectedModel);
        Assert.Equal("gpt-6-astra", viewModel.SelectedModel);
        Assert.Equal("xhigh", viewModel.SelectedReasoningEffort);

        viewModel.CaseSelectedModel = "gpt-5.6-luna";
        Assert.Contains(viewModel.AvailableCaseReasoningEfforts, item => item.Id == "medium");
        viewModel.CaseSelectedReasoningEffort = "medium";
        Assert.Equal("Luna / 中", viewModel.EffectiveSettingsDisplay);
        Assert.Contains(nameof(viewModel.EffectiveSettingsDisplay), notifications);
        Assert.DoesNotContain(temp.Path, viewModel.CaseSettingsDiagnostics);
        viewModel.PromptInput = "案件別設定で調査してください";
        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer == "回答です。", TimeSpan.FromSeconds(5));

        Assert.Equal("gpt-5.6-luna", caseModel);
        Assert.Equal("medium", caseReasoning);
        Assert.Equal("gpt-5.6-luna", fakeClient.LastRequestedModel);
        Assert.Equal("medium", fakeClient.LastRequestedReasoningEffort);
        Assert.Equal("gpt-6-astra", viewModel.SelectedModel);
        Assert.Equal("xhigh", viewModel.SelectedReasoningEffort);
        Assert.Equal("gpt-5.6-luna", viewModel.ActualModel);
        Assert.Equal("medium", viewModel.ActualReasoningEffort);
        caseModel = null;
        caseReasoning = null;
        viewModel.RefreshCaseSelection();
        Assert.Equal("Astra / 超高", viewModel.EffectiveSettingsDisplay);
        Assert.Equal("Luna / 中", viewModel.ActualThreadSettingsDisplay);
    }

    [Theory]
    [InlineData("low", "低")]
    [InlineData("medium", "中")]
    [InlineData("high", "高")]
    [InlineData("xhigh", "超高")]
    [InlineData("max", "最大")]
    [InlineData("ultra", "最上位")]
    [InlineData("future-effort", "future-effort")]
    [InlineData("-", "不明")]
    public void ReasoningDisplay_PreservesUnknownValues(string raw, string expected)
        => Assert.Equal(expected, CodexChatViewModel.DisplayReasoning(raw));

    [Theory]
    [InlineData("gpt-6-astra", "Astra")]
    [InlineData("gpt-5.6-sol", "Sol")]
    [InlineData("gpt-5.6-terra", "Terra")]
    [InlineData("gpt-5.6-luna", "Luna")]
    [InlineData("future-model", "future-model")]
    public void ModelDisplay_PreservesUnknownValues(string raw, string expected)
        => Assert.Equal(expected, CodexChatViewModel.DisplayModel(raw));

    [Fact]
    public async Task SendCommand_JoinsStreamingDeltasAndApplyDoesNotWriteFiles()
    {
        using var temp = new TempDirectory();
        var caseFile = Path.Combine(temp.Path, "case-note.txt");
        File.WriteAllText(caseFile, "original");
        var fakeClient = new FakeClient();
        string? applied = null;
        var canUndo = false;
        var undoCount = 0;
        var viewModel = new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "HelixQAC",
                SupportId = "0001",
                CaseFolder = temp.Path,
                InquiryText = "問い合わせ",
            },
            () => "fake.exe",
            text => { applied = text; canUndo = true; return true; },
            _ => true,
            _ => { canUndo = false; undoCount++; },
            canUndoApplication: isReply => isReply && canUndo);
        viewModel.PromptInput = "調査してください";
        await viewModel.InitializeAsync();

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer == "回答です。", TimeSpan.FromSeconds(5));
        viewModel.ApplyReplyCommand.Execute(null);

        Assert.Equal("回答です。", applied);
        Assert.True(viewModel.UndoReplyCommand.CanExecute(null));
        viewModel.UndoReplyCommand.Execute(null);
        Assert.Equal(1, undoCount);
        Assert.False(viewModel.UndoReplyCommand.CanExecute(null));
        Assert.Equal("original", File.ReadAllText(caseFile));
        Assert.Equal("回答です。", viewModel.Messages.Last().Text);
        Assert.False(viewModel.Messages.Last().IsStreaming);
    }

    [Fact]
    public async Task EditedTechnicalAnswer_IsUsedByReplyAndFinalReview()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        string? applied = null;
        var viewModel = new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "SyntheticProduct",
                SupportId = "SYN-EDIT-001",
                CaseFolder = temp.Path,
                InquiryText = "人工fixtureの問い合わせ",
            },
            () => "fake.exe",
            text => { applied = text; return true; },
            _ => true,
            _ => { });
        viewModel.PromptInput = "初回回答を生成してください";
        await viewModel.InitializeAsync();

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer == "回答です。", TimeSpan.FromSeconds(5));

        viewModel.TechnicalAnswer += Environment.NewLine + "[manual-edit-test]";
        viewModel.ApplyReplyCommand.Execute(null);

        Assert.Contains("[manual-edit-test]", applied, StringComparison.Ordinal);

        viewModel.FinalReviewCommand.Execute(null);
        await WaitUntilAsync(
            () => fakeClient.LastTurnText.Contains("[manual-edit-test]", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SendToWpfNoteCommand_SendsEditedAnswerToParentNoteEditor()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        string? sentText = null;
        var viewModel = new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "SyntheticProduct",
                SupportId = "SYN-NOTE-001",
                CaseFolder = temp.Path,
                InquiryText = "人工fixtureの問い合わせ",
                NoteEditorTransferPipeName = "synthetic-pipe",
            },
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { },
            sendToWpfNoteEditor: text =>
            {
                sentText = text;
                return Task.FromResult(true);
            });
        await viewModel.InitializeAsync();

        viewModel.StartNewCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.ThreadId == "thread-1" && viewModel.StartNewCommand.CanExecute(null),
            TimeSpan.FromSeconds(5));

        viewModel.TechnicalAnswer = "編集済みTechnicalAnswer";
        Assert.True(viewModel.SendToWpfNoteCommand.CanExecute(null));
        viewModel.SendToWpfNoteCommand.Execute(null);
        await WaitUntilAsync(() => sentText is not null, TimeSpan.FromSeconds(5));

        Assert.Equal("編集済みTechnicalAnswer", sentText);
        Assert.Contains("WPFノート編集へコピーしました", viewModel.ConnectionDetails, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManufacturerDraft_DoesNotExposeRawJsonInTechnicalAnswer()
    {
        using var temp = new TempDirectory();
        var correspondenceFolder = Directory.CreateDirectory(Path.Combine(temp.Path, "manufacturer-correspondence"));
        await File.WriteAllTextAsync(
            Path.Combine(correspondenceFolder.FullName, "メーカー連携内容_0001_latest.txt"),
            "Date: Tue, 02 Sep 2026 10:00:00 +0900\nFrom: John Smith <john.smith@manufacturer.example>\nSupport ID: 0001");
        var fakeClient = new FakeClient();
        fakeClient.EnqueueResponse(
            """
            {"japaneseDraft":"件名: HelixQAC 0001 確認\nメーカーサポートご担当者様\nお世話になっております。\n東陽テクニカの伊藤です。\nお客様からの確認事項について、質問1: HelixQAC 0001の検証方法をご教示ください。\nTranslated_File_EN.txtを添付します。\nどうぞよろしくお願いいたします。\n株式会社東陽テクニカ\n伊藤 健","englishDraft":"Subject: HelixQAC 0001 review\nHi John,\nThis is Ken Ito from Toyo Corporation.\nOur customer has asked us to confirm the details. Question 1: Please explain the validation method for HelixQAC 0001.\nWe attach Translated_File_EN.txt.\nBest regards,\nKen Ito\nToyo Corporation"}
            """);
        var viewModel = CreateViewModel(temp, fakeClient, "質問1：HelixQACの検証方法をご教示ください。");
        await viewModel.InitializeAsync();

        viewModel.TechnicalAnswer = "既存の技術回答案";
        viewModel.SelectedPreset = viewModel.PromptPresets.Single(
            preset => preset.Name == "メーカーへ確認・追加質問する");

        viewModel.SendCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!viewModel.JapaneseManufacturerDraft.Contains("質問1", StringComparison.Ordinal)
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.True(
            viewModel.JapaneseManufacturerDraft.Contains("質問1", StringComparison.Ordinal),
            $"ArtifactState={viewModel.ArtifactStateText}; Warning={viewModel.WarningText}; Error={viewModel.ErrorText}; Connection={viewModel.ConnectionDetails}; LastTurn={fakeClient.LastTurnText}");

        Assert.Equal("既存の技術回答案", viewModel.TechnicalAnswer);
        Assert.Contains("質問1", viewModel.JapaneseManufacturerDraft, StringComparison.Ordinal);
        Assert.Contains("HelixQAC", viewModel.JapaneseManufacturerDraft, StringComparison.Ordinal);
        Assert.Contains("Hi John,", viewModel.EnglishManufacturerDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("Hi John,", viewModel.TechnicalAnswer, StringComparison.Ordinal);
        Assert.DoesNotContain("japaneseDraft", viewModel.TechnicalAnswer, StringComparison.Ordinal);
        Assert.DoesNotContain("englishDraft", viewModel.TechnicalAnswer, StringComparison.Ordinal);
        Assert.Contains("メーカー担当者を特定できませんでした", viewModel.ManufacturerRecipientText, StringComparison.Ordinal);
        Assert.DoesNotContain("John Smith <john.smith@manufacturer.example>", fakeClient.LastTurnText, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolutionStatus:", fakeClient.LastTurnText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManufacturerValidationFailure_ShowsCategoryDetailsAndKeepsTransferClosed()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        fakeClient.EnqueueResponse(
            """{"japaneseDraft":"件名: HelixQAC 0001 確認\n本件をクローズしたいと考えています。","englishDraft":"Subject: 0001 review\nWe would like to close this case."}""");
        var viewModel = CreateViewModel(temp, fakeClient, "質問1：HelixQACの検証方法をご教示ください。");
        await viewModel.InitializeAsync();

        viewModel.JapaneseManufacturerDraft = "previous Japanese draft";
        viewModel.EnglishManufacturerDraft = "previous English draft";
        viewModel.PromptInput = "メーカー向け確認メール案を作成";
        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.ArtifactStateText == "警告あり",
            TimeSpan.FromSeconds(5));

        Assert.Contains("UNSUPPORTED_CLOSE_INTENT", viewModel.ErrorText, StringComparison.Ordinal);
        Assert.Contains("クローズ", viewModel.JapaneseManufacturerDraft, StringComparison.Ordinal);
        Assert.Contains("close this case", viewModel.EnglishManufacturerDraft, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.SendEnglishManufacturerDraftToWpfNoteCommand.CanExecute(null));
    }

    [Fact]
    public async Task ManufacturerReply_UsesImmediateResponseWithoutQuestionsOrTranslationPlan()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "お客様ご相談内容_00018729.txt"), "old customer inquiry");
        var fakeClient = new FakeClient();
        fakeClient.EnqueueResponse(
            """{"japaneseDraft":"件名: Amazon Linux 2023 サポートについて（Support ID: 0001）\nJim様\nお世話になっております。\nEngineeringチームへのご確認とご回答をありがとうございます。\nAmazon Linux 2023は2023.8までサポートされ、HelixQACのドキュメントにサポートが記載予定であると理解しました。\n新しいバージョンの検証にはインフラ準備が必要で、まだ予定されていないことも承知しました。\n改めてご支援ありがとうございます。\n東陽テクニカ\n伊藤 健","englishDraft":"Subject: Re: Amazon Linux 2023 Support (Support ID: 0001)\nHi Jim,\nThank you for checking with the Engineering team and for your update.\nWe understand that Amazon Linux 2023 is supported up to 2023.8 and that HelixQAC documentation will state this support.\nWe also understand that testing newer versions requires infrastructure setup and has not yet been scheduled.\nThank you again for your support and clarification.\nBest regards,\nKen Ito\nToyo Corporation"}""");
        var viewModel = CreateViewModel(temp, fakeClient, "old inquiry");
        await viewModel.InitializeAsync();
        viewModel.Messages.Add(new CodexChatMessageViewModel
        {
            Role = "user",
            Text = "Ken,\nI have heard back from engineering.\nWe support Amazon Linux 2023 up to 2023.8.\nFull testing has not yet been scheduled.\nRegards\nJim Weber | Principle Technical Support Engineer",
        });
        viewModel.TechnicalAnswer = "existing technical answer";
        viewModel.SelectedPreset = viewModel.PromptPresets.Single(
            preset => preset.Name == "メーカー回答へ返信する（御礼・受領）");
        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.ArtifactStateText is "完了" or "警告あり" || !string.IsNullOrWhiteSpace(viewModel.ErrorText),
            TimeSpan.FromSeconds(5));

        Assert.True(
            viewModel.ArtifactStateText == "完了",
            $"State={viewModel.ArtifactStateText}; Warning={viewModel.ArtifactWarnings}; Error={viewModel.ErrorText}; Prompt={fakeClient.LastTurnText}");
        Assert.Contains("Hi Jim,", viewModel.EnglishManufacturerDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("Question", viewModel.EnglishManufacturerDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Could you", viewModel.EnglishManufacturerDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, viewModel.ArtifactSourceFile);
        Assert.Equal("NONE", viewModel.ArtifactOutputPlanText);
        Assert.Contains("翻訳成果物計画: NONE", viewModel.ArtifactResultText, StringComparison.Ordinal);
        Assert.Equal("existing technical answer", viewModel.TechnicalAnswer);
        Assert.Contains("宛先: Jim", viewModel.ManufacturerRecipientText, StringComparison.Ordinal);
        Assert.Contains("GUI Send Route: MANUFACTURER", viewModel.ManufacturerFollowUpScopeText, StringComparison.Ordinal);
        Assert.Contains("Manufacturer Intent: REPLY_TO_MANUFACTURER", viewModel.ManufacturerFollowUpScopeText, StringComparison.Ordinal);
        Assert.Contains("Selected Preset Display: メーカー回答へ返信する（御礼・受領）", viewModel.ManufacturerFollowUpScopeText, StringComparison.Ordinal);
        Assert.Contains("Selected Preset Key: REPLY_TO_MANUFACTURER", viewModel.ManufacturerFollowUpScopeText, StringComparison.Ordinal);
        Assert.Contains("Selected Preset Kind: ManufacturerReply", viewModel.ManufacturerFollowUpScopeText, StringComparison.Ordinal);
        Assert.Contains("Preset Mapping Result: REPLY_TO_MANUFACTURER", viewModel.ManufacturerFollowUpScopeText, StringComparison.Ordinal);
        Assert.Contains("JP Draft Assigned: YES", viewModel.ManufacturerFollowUpScopeText, StringComparison.Ordinal);
        Assert.Contains("EN Draft Assigned: YES", viewModel.ManufacturerFollowUpScopeText, StringComparison.Ordinal);
        Assert.Contains("TechnicalAnswer Changed: NO", viewModel.ManufacturerFollowUpScopeText, StringComparison.Ordinal);
        Assert.Contains("Communication Intent: REPLY_TO_MANUFACTURER", fakeClient.LastTurnText, StringComparison.Ordinal);
        Assert.Contains("Jim Weber", fakeClient.LastTurnText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManufacturerReply_UsesRestoredCurrentThreadManufacturerResponse()
    {
        using var temp = new TempDirectory();
        var sessionStore = new CodexSessionStore(Path.Combine(temp.Path, "sessions.json"));
        await sessionStore.SaveAsync(new CodexSession
        {
            SupportId = "0001",
            CaseFolder = temp.Path,
            CodexThreadId = "restored-thread",
            Model = "fake",
            LastUsedAt = DateTimeOffset.Now,
            Messages =
            [
                new CodexSessionMessage
                {
                    Role = "user",
                    Text = "Ken,\nI have heard back from engineering.\nWe support Amazon Linux 2023 up to 2023.8.\nRegards\nJim Weber | Principle Technical Support Engineer",
                    CreatedAt = DateTimeOffset.Now.AddMinutes(-1),
                },
            ],
        });

        var fakeClient = new FakeClient();
        fakeClient.EnqueueResponse(
            """{"japaneseDraft":"件名: HelixQAC 受領（Support ID: 0001）\nJim様\nHelixQACについてご回答ありがとうございます。\n東陽テクニカ\n伊藤 健","englishDraft":"Subject: Re: HelixQAC Support (Support ID: 0001)\nHi Jim,\nThank you for your HelixQAC response.\nBest regards,\nKen Ito"}""");
        var viewModel = new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            sessionStore,
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "HelixQAC",
                SupportId = "0001",
                CaseFolder = temp.Path,
                InquiryText = "確認してください。",
            },
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { });

        await viewModel.InitializeAsync();
        viewModel.ResumeCommand.Execute(null);
        await WaitUntilAsync(() => fakeClient.CurrentThreadId == "restored-thread", TimeSpan.FromSeconds(5));
        viewModel.SelectedPreset = viewModel.PromptPresets.Single(
            preset => preset.Name == "メーカー回答へ返信する（御礼・受領）");
        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.ArtifactStateText is "完了" or "警告あり" || !string.IsNullOrWhiteSpace(viewModel.ErrorText),
            TimeSpan.FromSeconds(5));

        Assert.True(
            viewModel.ArtifactStateText == "完了",
            $"State={viewModel.ArtifactStateText}; Warning={viewModel.ArtifactWarnings}; Error={viewModel.ErrorText}; Prompt={fakeClient.LastTurnText}");
        Assert.Contains("Jim Weber", fakeClient.LastTurnText, StringComparison.Ordinal);
        Assert.Contains("Hi Jim,", viewModel.EnglishManufacturerDraft, StringComparison.Ordinal);
        Assert.DoesNotContain("MANUFACTURER_RESPONSE_INCOMPLETE", viewModel.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManufacturerReply_DoesNotTreatCustomerInquiryAsManufacturerResponse()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        var viewModel = CreateViewModel(temp, fakeClient, "お客様からの追加質問です。回答をお願いします。");
        await viewModel.InitializeAsync();
        viewModel.SelectedPreset = viewModel.PromptPresets.Single(
            preset => preset.Name == "メーカー回答へ返信する（御礼・受領）");
        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ArtifactStateText == "警告あり", TimeSpan.FromSeconds(5));

        Assert.Contains("MANUFACTURER_RESPONSE_INCOMPLETE", viewModel.ErrorText, StringComparison.Ordinal);
        Assert.Equal(0, fakeClient.TurnCount);
    }

    [Fact]
    public async Task ManufacturerReply_RejectsUnexpectedQuestions()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        fakeClient.EnqueueResponse(
            """{"japaneseDraft":"件名: 受領\nご回答ありがとうございます。質問1: 詳細をご教示ください。Klocwork 0001","englishDraft":"Subject: Thanks\nThank you. Question 1: Could you clarify this? Klocwork 0001"}""");
        var viewModel = CreateViewModel(temp, fakeClient);
        await viewModel.InitializeAsync();
        viewModel.Messages.Add(new CodexChatMessageViewModel { Role = "user", Text = "Engineering has replied. Regards\nJim Weber | Support Engineer" });
        viewModel.PromptInput = CodexPromptPreset.ManufacturerReplyPrompt;
        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.ArtifactStateText == "警告あり" || !string.IsNullOrWhiteSpace(viewModel.ErrorText),
            TimeSpan.FromSeconds(5));

        Assert.Contains("UNEXPECTED_QUESTION_GENERATION", viewModel.ErrorText, StringComparison.Ordinal);
        Assert.False(viewModel.SendEnglishManufacturerDraftToWpfNoteCommand.CanExecute(null));
    }

    [Fact]
    public async Task SendCommand_NormalizesLegacyLogAndSendsImageInput()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var temp = new TempDirectory();
        var logPath = Path.Combine(temp.Path, "trace.log");
        var imagePath = Path.Combine(temp.Path, "error-screen.png");
        await File.WriteAllTextAsync(logPath, "権限が不足しています。エラーコード: E_UPLOAD_42", Encoding.GetEncoding(932));
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4e, 0x47]);
        var fakeClient = new FakeClient();
        var viewModel = CreateViewModel(temp, fakeClient);
        viewModel.PromptInput = "原因を調査してください";
        await viewModel.InitializeAsync();

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer == "回答です。", TimeSpan.FromSeconds(5));

        Assert.Contains("権限が不足しています", fakeClient.LastTurnText);
        Assert.Contains("E_UPLOAD_42", fakeClient.LastTurnText);
        Assert.Contains("CP932", fakeClient.LastTurnText);
        Assert.Contains(imagePath, fakeClient.LastImagePaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("本文読取済み (UTF-8正規化)", viewModel.Files.Single(file => file.FullPath == logPath).ConfirmationStatus);
        Assert.Equal("画像入力として送信", viewModel.Files.Single(file => file.FullPath == imagePath).ConfirmationStatus);
    }

    [Fact]
    public async Task Initialize_WhenCaseFolderIsMissing_DisablesThreadAndSendCommandsWithReason()
    {
        using var temp = new TempDirectory();
        var missingFolder = Path.Combine(temp.Path, "missing");
        var fakeClient = new FakeClient();
        var viewModel = new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "Checkmarx",
                SupportId = "00018249",
                CaseFolder = missingFolder,
                InquiryText = "問い合わせ",
            },
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { });
        viewModel.PromptInput = "調査してください";

        await viewModel.InitializeAsync();

        Assert.False(viewModel.SendCommand.CanExecute(null));
        Assert.False(viewModel.StartNewCommand.CanExecute(null));
        Assert.Contains("送信できません", viewModel.CaseFolderSendStatus);
        Assert.Contains("案件フォルダが見つかりません", viewModel.CaseFolderSendStatus);
    }

    [Fact]
    public async Task Initialize_WhenCaseFolderWasRenamed_RestoresSavedChatImmediately()
    {
        using var temp = new TempDirectory();
        var productId = Guid.NewGuid();
        var movedFolder = Path.Combine(temp.Path, "00018250_メーカー確認中");
        Directory.CreateDirectory(movedFolder);
        var sessionStore = new CodexSessionStore(Path.Combine(temp.Path, "sessions.json"));
        await sessionStore.SaveAsync(new CodexSession
        {
            SupportId = "00018250",
            ProductId = productId,
            CaseFolder = Path.Combine(temp.Path, "00018250_受付"),
            CodexThreadId = "saved-thread",
            Model = "saved-model",
            LastUsedAt = DateTimeOffset.Now,
            Messages =
            [
                new CodexSessionMessage { Role = "user", Text = "前回の質問", CreatedAt = DateTimeOffset.Now.AddMinutes(-1) },
                new CodexSessionMessage { Role = "assistant", Text = "前回の回答", CreatedAt = DateTimeOffset.Now },
            ],
        });
        var viewModel = new CodexChatViewModel(
            new FakeClient(),
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            sessionStore,
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductId = productId,
                ProductName = "Checkmarx",
                SupportId = "00018250",
                CaseFolder = movedFolder,
                InquiryText = "問い合わせ",
            },
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { });

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.Messages.Count);
        Assert.Equal("前回の質問", viewModel.Messages[0].Text);
        Assert.Equal("前回の回答", viewModel.TechnicalAnswer);
        Assert.Equal("saved-thread", viewModel.ThreadId);
        Assert.Equal("saved-model", viewModel.Model);
        Assert.True(viewModel.ResumeCommand.CanExecute(null));
        Assert.Contains("チャット履歴を復元しました", viewModel.PreviousSessionStatus);
    }

    [Fact]
    public async Task SendCommand_RagLabEvidenceOff_DoesNotCallLoaderOrChangePrompt()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        var loader = new FakeRagLabEvidenceLoader(CreateEvidenceResult());
        var snapshot = new CodexCaseSnapshot
        {
            ProductName = "Checkmarx",
            SupportId = "SYN-CASE",
            CaseFolder = temp.Path,
            InquiryText = "人工問い合わせ",
            UseRagLabEvidence = false,
        };
        var viewModel = CreateViewModel(temp, fakeClient, snapshot, loader);
        viewModel.PromptInput = "調査してください";
        await viewModel.InitializeAsync();

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer == "回答です。", TimeSpan.FromSeconds(5));

        var expected = new CodexPromptComposer(temp.Path).ComposeInitialPrompt(new CodexInitialPromptContext
        {
            ProductName = snapshot.ProductName,
            SupportId = snapshot.SupportId,
            CaseFolder = snapshot.CaseFolder,
            InquiryText = snapshot.InquiryText,
            UserInstruction = "調査してください",
        }).Prompt;
        Assert.Equal(0, loader.CallCount);
        Assert.Equal(expected, fakeClient.LastTurnText);
        Assert.DoesNotContain("[RAG Evidence]", fakeClient.LastTurnText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendCommand_RagLabEvidenceOn_AddsEvidenceToCodexPrompt()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        var loader = new FakeRagLabEvidenceLoader(CreateEvidenceResult());
        var snapshot = new CodexCaseSnapshot
        {
            ProductName = "Checkmarx",
            SupportId = "SYN-CASE",
            CaseFolder = temp.Path,
            InquiryText = "人工問い合わせ",
            UseRagLabEvidence = true,
            RagLabEvidenceFilePath = "evidence.json",
            RagLabBaselineReadinessFilePath = "readiness.json",
            RagLabEvidenceMaxItems = 3,
            TargetVersion = "SYNTHETIC-1.0",
        };
        var viewModel = CreateViewModel(temp, fakeClient, snapshot, loader);
        viewModel.PromptInput = "調査してください";
        await viewModel.InitializeAsync();

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer == "回答です。", TimeSpan.FromSeconds(5));

        Assert.Equal(1, loader.CallCount);
        Assert.Equal("Checkmarx", loader.LastRequest?.ExpectedProduct);
        Assert.Equal("SYNTHETIC-1.0", loader.LastRequest?.ExpectedVersion);
        Assert.Contains("[RAG Evidence]", fakeClient.LastTurnText, StringComparison.Ordinal);
        Assert.Contains("人工追加根拠", fakeClient.LastTurnText, StringComparison.Ordinal);
        Assert.Contains("[End RAG Evidence]", fakeClient.LastTurnText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendCommand_RagLabLoaderThrows_ContinuesUsingExistingPrompt()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        var loader = new FakeRagLabEvidenceLoader(new IOException("人工読込失敗"));
        var snapshot = new CodexCaseSnapshot
        {
            ProductName = "Checkmarx",
            SupportId = "SYN-CASE",
            CaseFolder = temp.Path,
            InquiryText = "人工問い合わせ",
            UseRagLabEvidence = true,
        };
        var viewModel = CreateViewModel(temp, fakeClient, snapshot, loader);
        viewModel.PromptInput = "調査してください";
        await viewModel.InitializeAsync();

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer == "回答です。", TimeSpan.FromSeconds(5));

        Assert.Equal(1, loader.CallCount);
        Assert.DoesNotContain("[RAG Evidence]", fakeClient.LastTurnText, StringComparison.Ordinal);
        Assert.Contains("従来経路で続行", viewModel.WarningText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyReply_WhenRagLabInternalTermsRemain_DoesNotUpdateCustomerDraft()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        fakeClient.EnqueueResponse("[RAG Evidence] 内部情報を含む回答");
        string? applied = null;
        var snapshot = new CodexCaseSnapshot
        {
            ProductName = "Checkmarx",
            SupportId = "SYN-CASE",
            CaseFolder = temp.Path,
            InquiryText = "人工問い合わせ",
            UseRagLabEvidence = true,
        };
        var viewModel = CreateViewModel(
            temp,
            fakeClient,
            snapshot,
            new FakeRagLabEvidenceLoader(CreateEvidenceResult()),
            text => { applied = text; return true; });
        viewModel.PromptInput = "調査してください";
        await viewModel.InitializeAsync();

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer.Contains("[RAG Evidence]", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        viewModel.ApplyReplyCommand.Execute(null);

        Assert.Null(applied);
        Assert.Contains("返信案への反映を中止", viewModel.WarningText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbComparison_WithSyntheticData_RecordsTwoUserRunsWithoutPersistingAnswerText()
    {
        using var temp = new TempDirectory();
        var fakeClient = new FakeClient();
        var logger = new FakeLogger(temp.Path);
        var loader = new FakeRagLabEvidenceLoader(CreateEvidenceResult());
        var snapshot = new CodexCaseSnapshot
        {
            ProductName = "SyntheticProduct",
            SupportId = "SYN-AB-001",
            CompanyName = "Synthetic Company",
            CaseFolder = temp.Path,
            InquiryText = "人工機能の生成方法を教えてください。",
            Evidence =
            [
                new SearchSource { SourceType = "OfficialDoc", Title = "Synthetic Official", Text = "人工公式根拠" },
                new SearchSource { SourceType = "PastCaseNote", Title = "Synthetic Case", Text = "人工過去案件" },
            ],
            UseRagLabEvidence = false,
        };
        var viewModel = new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            logger,
            () => snapshot,
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { },
            ragLabEvidenceLoader: loader);
        await viewModel.InitializeAsync();

        fakeClient.EnqueueResponse("手順:\n1. version 1.0 を確認します。\n要確認: 人工OS");
        viewModel.PromptInput = "人工調査を実行してください";
        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer.Contains("version 1.0", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => viewModel.CaptureAbBaselineCommand.CanExecute(null), TimeSpan.FromSeconds(5));

        Assert.True(viewModel.CaptureAbBaselineCommand.CanExecute(null));
        Assert.False(viewModel.CaptureAbEvidenceCommand.CanExecute(null));
        viewModel.CaptureAbBaselineCommand.Execute(null);
        viewModel.PromptInput = "人工調査を実行してください";
        await WaitUntilAsync(() => viewModel.SendCommand.CanExecute(null), TimeSpan.FromSeconds(5));

        snapshot = snapshot with
        {
            UseRagLabEvidence = true,
            RagLabEvidenceFilePath = "synthetic-evidence.json",
            RagLabBaselineReadinessFilePath = "synthetic-readiness.json",
            TargetVersion = "SYNTHETIC-1.0",
        };
        viewModel.StartNewCommand.Execute(null);
        await WaitUntilAsync(
            () => string.IsNullOrEmpty(viewModel.TechnicalAnswer)
                && viewModel.ConnectionDetails.Contains("新しい読み取り専用Thread", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => viewModel.StartNewCommand.CanExecute(null), TimeSpan.FromSeconds(5));

        fakeClient.EnqueueResponse("手順:\n1. version 2.0 を確認します。\n2. `synthetic-cli run` を実行します。");
        await WaitUntilAsync(() => viewModel.SendCommand.CanExecute(null), TimeSpan.FromSeconds(5));
        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.TechnicalAnswer.Contains("version 2.0", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => viewModel.CaptureAbEvidenceCommand.CanExecute(null), TimeSpan.FromSeconds(5));

        Assert.True(viewModel.CaptureAbEvidenceCommand.CanExecute(null));
        viewModel.CaptureAbEvidenceCommand.Execute(null);
        Assert.True(viewModel.CompareAbCommand.CanExecute(null));
        viewModel.CompareAbCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.AbComparisonText.Contains("技術値・コマンド差分", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        Assert.Equal(2, fakeClient.TurnCount);
        Assert.Contains("A: 回答可能判定=回答あり", viewModel.AbComparisonText, StringComparison.Ordinal);
        Assert.Contains("B: 回答可能判定=回答あり", viewModel.AbComparisonText, StringComparison.Ordinal);
        Assert.Contains("公式=1, Manual=1, PastCase=1", viewModel.AbComparisonText, StringComparison.Ordinal);
        Assert.Contains("自動判定しません", viewModel.AbComparisonText, StringComparison.Ordinal);
        var comparisonLog = Assert.Single(logger.Entries, entry => entry.Category == "rag-lab-ab-comparison");
        Assert.DoesNotContain("version 1.0", comparisonLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("version 2.0", comparisonLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic Company", comparisonLog.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtifactCommands_RequirePlanThenCreateExcelAndManufacturerMail()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "問い合わせ内容.xlsx");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var fakeClient = new FakeClient();
        var fakeArtifactService = new FakeExcelTranslationService();
        string? copiedText = null;
        string? noteText = null;
        var viewModel = new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "Checkmarx",
                SupportId = "00018290",
                CompanyName = "Test Company",
                CaseFolder = temp.Path,
                InquiryText = "質問1：翻訳の検証方法をご教示ください。",
                NoteEditorTransferPipeName = "synthetic-note-pipe",
            },
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { },
            excelTranslationService: fakeArtifactService,
            artifactPromptComposer: new ArtifactPromptComposer(temp.Path),
            sendToWpfNoteEditor: text =>
            {
                noteText = text;
                return Task.FromResult(true);
            },
            clipboardWriter: text => copiedText = text);
        viewModel.PromptInput = "問い合わせ内容.xlsxを英語に翻訳して別名で保存してください";
        await viewModel.InitializeAsync();

        viewModel.PrepareArtifactPlanCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ArtifactStateText == "ユーザー確認待ち", TimeSpan.FromSeconds(5));

        Assert.Equal(0, fakeArtifactService.CreateCount);
        Assert.False(File.Exists(Path.Combine(temp.Path, "Inquiry_Details_EN.xlsx")));
        Assert.Equal("Inquiry_Details_EN.xlsx", viewModel.ArtifactOutputFileName);
        Assert.Single(viewModel.ArtifactTranslationPreview);
        Assert.True(viewModel.CreateExcelArtifactCommand.CanExecute(null));

        viewModel.ArtifactOutputFileName = "Inquiry_Details_EN_2.xlsx";

        Assert.Equal("再確認待ち", viewModel.ArtifactStateText);
        Assert.False(viewModel.CreateExcelArtifactCommand.CanExecute(null));

        viewModel.PrepareArtifactPlanCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ArtifactStateText == "ユーザー確認待ち", TimeSpan.FromSeconds(5));

        Assert.True(viewModel.CreateExcelArtifactCommand.CanExecute(null));

        fakeClient.EnqueueResponse(
            """[{"sheet":"Sheet1","cell":"A1","sourceText":"日本語","translatedText":"English"}]""");
        fakeClient.EnqueueResponse(
            """
            {"japaneseDraft":"件名: Checkmarx 問い合わせ内容の確認（Support ID: 00018290）\nメーカーサポートご担当者様\nお世話になっております。\n東陽テクニカの伊藤です。\nサポートID 00018290のお客様から翻訳内容の確認依頼を受領しました。\n今回の送付対象はInquiry_Details_EN_2.xlsxです。質問1: 翻訳の検証方法をご教示ください。\nどうぞよろしくお願いいたします。\n株式会社東陽テクニカ\n伊藤 健","englishDraft":"Subject: Checkmarx inquiry review (Support ID: 00018290)\nHello Support Team,\nThis is Ken Ito from Toyo Corporation.\nOur customer asked us to review the translated content for Support ID 00018290. The current attachment is Inquiry_Details_EN_2.xlsx. Question 1: Please explain the validation of the translation.\nBest regards,\nKen Ito\nToyo Corporation"}
            """);
        viewModel.CreateExcelArtifactCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ArtifactStateText == "完了", TimeSpan.FromSeconds(5));

        Assert.Equal(1, fakeArtifactService.CreateCount);
        Assert.True(File.Exists(viewModel.CreatedArtifactPath));
        Assert.True(
            viewModel.JapaneseManufacturerDraft.Contains("質問1", StringComparison.Ordinal),
            $"ArtifactState={viewModel.ArtifactStateText}; Warning={viewModel.ArtifactWarnings}; Error={viewModel.ErrorText}; Connection={viewModel.ConnectionDetails}");
        Assert.Contains("Hello Support Team,", viewModel.EnglishManufacturerDraft);
        Assert.Contains("Best regards,", viewModel.EnglishManufacturerDraft);
        Assert.Equal(viewModel.EnglishManufacturerDraft, viewModel.ManufacturerMailDraft);
        Assert.Contains("Runtime Manufacturer Mail Context:", viewModel.ManufacturerFollowUpScopeText);
        Assert.Contains("Runtime Case: 00018290", viewModel.ManufacturerFollowUpScopeText);
        Assert.Contains("Selected Translation Source: 問い合わせ内容.xlsx", viewModel.ManufacturerFollowUpScopeText);
        Assert.Contains("Artifact Source: 問い合わせ内容.xlsx", viewModel.ManufacturerFollowUpScopeText);
        Assert.Contains("Artifact Output: Inquiry_Details_EN_2.xlsx", viewModel.ManufacturerFollowUpScopeText);
        Assert.Contains("Current Outbound Attachment: Inquiry_Details_EN_2.xlsx", viewModel.ManufacturerFollowUpScopeText);
        Assert.Contains("Close Requested: FALSE", viewModel.ManufacturerFollowUpScopeText);
        Assert.Contains("Customer Intent Contains Close: NO", viewModel.ManufacturerFollowUpScopeText);
        Assert.Contains("Protected Values Expected:", viewModel.ManufacturerFollowUpScopeText);
        Assert.Contains("Protected Values Injected:", viewModel.ManufacturerFollowUpScopeText);
        Assert.Contains("Protected Values Missing: 0", viewModel.ManufacturerFollowUpScopeText);
        Assert.Contains("Protected Value Parity: PASS", viewModel.ManufacturerFollowUpScopeText);
        Assert.DoesNotContain(temp.Path, viewModel.ManufacturerFollowUpScopeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Test Company", viewModel.ManufacturerFollowUpScopeText, StringComparison.Ordinal);
        Assert.Null(noteText);
        viewModel.JapaneseManufacturerDraft += "\n社内確認済み";
        viewModel.EnglishManufacturerDraft += "\nEdited for the manufacturer.";
        viewModel.CopyJapaneseManufacturerDraftCommand.Execute(null);
        Assert.Contains("社内確認済み", copiedText);
        viewModel.CopyEnglishManufacturerDraftCommand.Execute(null);
        Assert.Equal(viewModel.EnglishManufacturerDraft, copiedText);
        await WaitUntilAsync(
            () => viewModel.SendEnglishManufacturerDraftToWpfNoteCommand.CanExecute(null),
            TimeSpan.FromSeconds(5));
        Assert.True(viewModel.SendEnglishManufacturerDraftToWpfNoteCommand.CanExecute(null));
        viewModel.SendEnglishManufacturerDraftToWpfNoteCommand.Execute(null);
        await WaitUntilAsync(() => noteText is not null, TimeSpan.FromSeconds(5));
        Assert.Equal(viewModel.EnglishManufacturerDraft, noteText);
        Assert.DoesNotContain("質問1", noteText, StringComparison.Ordinal);
        Assert.Equal("English", viewModel.ArtifactTranslationPreview.Single().TranslatedText);
        Assert.Equal("English", viewModel.ArtifactPreviewItems.Single().TranslatedText);
        Assert.False(viewModel.CreateExcelArtifactCommand.CanExecute(null));

        // Keep the successful old artifact, then change the live output and retry.
        viewModel.ArtifactOutputFileName = "Additional_Inquiry_Details_EN.xlsx";
        viewModel.TechnicalAnswer = "We would like to close this case. Inquiry_Details_EN.xlsx";
        fakeClient.EnqueueResponse("""{"japaneseDraft":"件名: Checkmarx 追加確認（Support ID: 00018290）\nメーカーサポートご担当者様\nお世話になっております。\n東陽テクニカの伊藤です。\nSupport ID 00018290について、Additional_Inquiry_Details_EN.xlsxを添付します。\nどうぞよろしくお願いいたします。\n株式会社東陽テクニカ\n伊藤 健","englishDraft":"Subject: Checkmarx follow-up (Support ID: 00018290)\nHello Support Team,\nThis is Ken Ito from Toyo Corporation.\nFor Support ID 00018290, the current attachment is Additional_Inquiry_Details_EN.xlsx.\nBest regards,\nKen Ito\nToyo Corporation"}""");
        viewModel.GenerateManufacturerMailCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ArtifactStateText == "完了", TimeSpan.FromSeconds(5));
        Assert.Contains("Additional_Inquiry_Details_EN.xlsx", fakeClient.LastTurnText);
        Assert.DoesNotContain("Inquiry_Details_EN_2.xlsx", fakeClient.LastTurnText);
        Assert.Contains("Additional_Inquiry_Details_EN.xlsx", viewModel.JapaneseManufacturerDraft);
        Assert.Contains("Additional_Inquiry_Details_EN.xlsx", viewModel.EnglishManufacturerDraft);
        Assert.True(viewModel.SendEnglishManufacturerDraftToWpfNoteCommand.CanExecute(null));

        // Retry with malformed output preserves the previous diagnostic candidate.
        fakeClient.EnqueueResponse("invalid JSON");
        viewModel.GenerateManufacturerMailCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.GenerateManufacturerMailCommand.CanExecute(null), TimeSpan.FromSeconds(5));
        Assert.Contains("Inquiry_Details_EN.xlsx", viewModel.EnglishManufacturerDraft);
        Assert.False(viewModel.SendEnglishManufacturerDraftToWpfNoteCommand.CanExecute(null));
    }

    [Fact]
    public async Task ArtifactInitialization_PrefersOriginalSourceOverPreviousTranslation()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "問い合わせ内容.xlsx");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "問い合わせ内容_EN.xlsx"), [4, 5, 6]);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "問い合わせ内容_EN_2.xlsx"), [7, 8, 9]);
        var viewModel = CreateViewModel(temp, new FakeClient());

        await viewModel.InitializeAsync();

        Assert.Equal(source, viewModel.ArtifactSourceFile);
        Assert.Equal("Inquiry_Details_EN.xlsx", viewModel.ArtifactOutputFileName);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (!condition() && DateTime.UtcNow - started < timeout)
        {
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    private static CodexChatViewModel CreateViewModel(TempDirectory temp, FakeClient fakeClient, string inquiry = "確認してください。")
    {
        return new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "HelixQAC",
                SupportId = "0001",
                CaseFolder = temp.Path,
                InquiryText = inquiry,
            },
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { });
    }

    private static CodexChatViewModel CreateViewModelWithSelection(
        TempDirectory temp,
        FakeClient fakeClient,
        string model,
        string reasoningEffort)
    {
        var savedModel = model;
        var savedReasoningEffort = reasoningEffort;
        return new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => new CodexCaseSnapshot
            {
                ProductName = "HelixQAC",
                SupportId = "0001",
                CaseFolder = temp.Path,
                InquiryText = "確認してください。",
            },
            () => "fake.exe",
            _ => true,
            _ => true,
            _ => { },
            codexSelectionProvider: () => (savedModel, savedReasoningEffort),
            codexSelectionUpdated: (updatedModel, updatedReasoningEffort) =>
            {
                savedModel = updatedModel ?? string.Empty;
                savedReasoningEffort = updatedReasoningEffort ?? string.Empty;
            });
    }

    private static CodexChatViewModel CreateViewModel(
        TempDirectory temp,
        FakeClient fakeClient,
        CodexCaseSnapshot snapshot,
        IRagLabEvidenceLoader loader,
        Func<string, bool>? applyReply = null)
    {
        return new CodexChatViewModel(
            fakeClient,
            new CodexCaseFileScanner(),
            new CodexPromptComposer(temp.Path),
            new CodexSessionStore(Path.Combine(temp.Path, "sessions.json")),
            new CodexTechnicalValueDiffDetector(),
            new FakeLogger(temp.Path),
            () => snapshot,
            () => "fake.exe",
            applyReply ?? (_ => true),
            _ => true,
            _ => { },
            ragLabEvidenceLoader: loader);
    }

    private static RagLabEvidenceLoadResult CreateEvidenceResult()
    {
        return new RagLabEvidenceLoadResult
        {
            IsEnabled = true,
            IsBaselineReady = true,
            Query = "人工問い合わせ",
            Evidence =
            [
                new RagLabEvidenceItem
                {
                    SourceType = "SyntheticManual",
                    DocumentId = "doc-1",
                    Product = "Checkmarx",
                    Version = "SYNTHETIC-1.0",
                    Score = 0.9,
                    SelectionReason = "人工選定理由",
                    Text = "人工追加根拠",
                },
            ],
        };
    }

    private sealed class FakeClient : ICodexAppServerClient
    {
        private readonly Queue<string> responses = new();
        public event EventHandler<CodexConnectionState>? StateChanged;
        public event EventHandler<CodexAgentMessageDeltaEventArgs>? AgentMessageDelta;
        public event EventHandler<CodexTurnCompletedEventArgs>? TurnCompleted;
        public event EventHandler<CodexItemEventArgs>? ItemStarted { add { } remove { } }
        public event EventHandler<CodexItemEventArgs>? ItemCompleted { add { } remove { } }
        public event EventHandler<string>? Warning { add { } remove { } }
        public event EventHandler<string>? Error { add { } remove { } }

        public CodexConnectionState State { get; private set; } = CodexConnectionState.Disconnected;
        public CodexConnectionInfo? ConnectionInfo { get; private set; }
        public string? CurrentThreadId { get; private set; }
        public string? CurrentTurnId { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public string LastTurnText { get; private set; } = string.Empty;
        public IReadOnlyList<string> LastImagePaths { get; private set; } = [];
        public int TurnCount { get; private set; }
        public string LastRequestedModel { get; private set; } = string.Empty;
        public string LastRequestedReasoningEffort { get; private set; } = string.Empty;
        public IReadOnlyList<CodexModelInfo> Models { get; set; } =
            [new CodexModelInfo("fake", "Fake", true, false, "medium")];

        public void EnqueueResponse(string response)
        {
            responses.Enqueue(response);
        }

        public Task<CodexConnectionInfo> ConnectAsync(string? configuredExecutablePath, CancellationToken cancellationToken = default)
        {
            ConnectionInfo = new CodexConnectionInfo(
                "fake.exe",
                "0.145.0",
                "fake",
                new CodexAccountInfo { AccountType = "chatgpt", PlanType = "plus" },
                Models);
            State = CodexConnectionState.Connected;
            StateChanged?.Invoke(this, State);
            return Task.FromResult(ConnectionInfo);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = CodexConnectionState.Disconnected;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public Task<CodexThreadStartResult> StartThreadAsync(string workingDirectory, string? model, CancellationToken cancellationToken = default)
        {
            LastRequestedModel = model ?? string.Empty;
            CurrentThreadId = "thread-1";
            WorkingDirectory = workingDirectory;
            return Task.FromResult(new CodexThreadStartResult("thread-1", model ?? "fake", workingDirectory, "read-only"));
        }

        public Task<CodexThreadStartResult> ResumeThreadAsync(string threadId, string workingDirectory, string? model, CancellationToken cancellationToken = default)
        {
            CurrentThreadId = threadId;
            WorkingDirectory = workingDirectory;
            return Task.FromResult(new CodexThreadStartResult(threadId, "fake", workingDirectory, "read-only"));
        }

        public Task<CodexTurnStartResult> StartTurnAsync(
            string text,
            IReadOnlyList<string>? localImagePaths = null,
            string? model = null,
            string? reasoningEffort = null,
            CancellationToken cancellationToken = default)
        {
            TurnCount++;
            LastRequestedModel = model ?? LastRequestedModel;
            LastRequestedReasoningEffort = reasoningEffort ?? string.Empty;
            LastTurnText = text;
            LastImagePaths = localImagePaths?.ToArray() ?? [];
            CurrentTurnId = "turn-1";
            var response = responses.Count > 0 ? responses.Dequeue() : "回答です。";
            AgentMessageDelta?.Invoke(this, new CodexAgentMessageDeltaEventArgs("thread-1", "turn-1", "item-1", response));
            CurrentTurnId = null;
            TurnCompleted?.Invoke(this, new CodexTurnCompletedEventArgs("thread-1", "turn-1", "completed", null));
            return Task.FromResult(new CodexTurnStartResult("turn-1", model ?? "fake", reasoningEffort ?? "medium"));
        }

        public Task<CodexTurnStartResult> StartIsolatedDraftTurnAsync(
            string briefPrompt,
            string? model,
            string? reasoningEffort,
            CancellationToken cancellationToken = default)
        {
            return StartTurnAsync(briefPrompt, model: model, reasoningEffort: reasoningEffort, cancellationToken: cancellationToken);
        }

        public Task InterruptTurnAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLogger(string path) : ICodexDiagnosticLogger
    {
        public string LogDirectory { get; } = path;
        public List<(string Category, string Message)> Entries { get; } = [];

        public Task WriteAsync(string category, string message, Exception? exception = null, CancellationToken cancellationToken = default)
        {
            Entries.Add((category, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRagLabEvidenceLoader : IRagLabEvidenceLoader
    {
        private readonly RagLabEvidenceLoadResult? result;
        private readonly Exception? exception;

        public FakeRagLabEvidenceLoader(RagLabEvidenceLoadResult result)
        {
            this.result = result;
        }

        public FakeRagLabEvidenceLoader(Exception exception)
        {
            this.exception = exception;
        }

        public int CallCount { get; private set; }
        public RagLabEvidenceLoadRequest? LastRequest { get; private set; }

        public Task<RagLabEvidenceLoadResult> LoadAsync(
            RagLabEvidenceLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return exception is null
                ? Task.FromResult(result!)
                : Task.FromException<RagLabEvidenceLoadResult>(exception);
        }
    }

    private sealed class FakeExcelTranslationService : IExcelTranslationService
    {
        public int CreateCount { get; private set; }

        public Task<ArtifactCreationPlan> CreatePlanAsync(
            ArtifactCreationRequest request,
            CancellationToken cancellationToken = default)
        {
            var destination = string.IsNullOrWhiteSpace(request.DestinationFolder)
                ? request.CaseFolder
                : request.DestinationFolder;
            var entry = new ExcelTranslationEntry
            {
                Sheet = "Sheet1",
                Cell = "A1",
                SourceText = "日本語",
                ShouldTranslate = true,
                NumberFormat = "General",
            };
            return Task.FromResult(new ArtifactCreationPlan
            {
                Request = request,
                CaseFolderFullPath = request.CaseFolder,
                SourceFullPath = request.SourceFilePath,
                DestinationFullPath = destination,
                OutputFullPath = Path.Combine(destination, request.OutputFileName),
                SourceSha256 = "fake",
                DestinationFolderWillBeCreated = !Directory.Exists(destination),
                Excel = new ExcelTranslationPlan { Entries = [entry] },
            });
        }

        public async Task<ArtifactCreationResult> CreateArtifactAsync(
            ArtifactCreationPlan plan,
            IReadOnlyList<ExcelTranslationValue> translations,
            CancellationToken cancellationToken = default)
        {
            CreateCount++;
            Directory.CreateDirectory(plan.DestinationFullPath);
            await File.WriteAllTextAsync(plan.OutputFullPath, "created", cancellationToken);
            return new ArtifactCreationResult
            {
                Succeeded = true,
                OutputFilePath = plan.OutputFullPath,
                TranslationTargetCount = translations.Count,
                TranslatedCount = translations.Count,
            };
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexChatViewModelTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
