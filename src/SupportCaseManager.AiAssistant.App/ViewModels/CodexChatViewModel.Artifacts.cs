using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Artifacts;
using FormsDialogResult = System.Windows.Forms.DialogResult;
using FormsFolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using WpfClipboard = System.Windows.Clipboard;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed partial class CodexChatViewModel
{
    private ArtifactCreationPlan? artifactPlan;
    private ArtifactCreationResult? artifactResult;
    private IReadOnlyList<ExcelTranslationValue> artifactTranslations = [];
    private TaskCompletionSource<string>? artifactTurnCompletion;
    private bool artifactPlanReadyForExecution;
    private string artifactStateText = "未計画";
    private int artifactProgressPercent;
    private string artifactSourceFile = string.Empty;
    private string artifactDestinationFolder = string.Empty;
    private string artifactOutputFileName = "Inquiry_Details_EN.xlsx";
    private string artifactRequestInstruction = string.Empty;
    private string artifactWarnings = string.Empty;
    private string artifactResultText = string.Empty;
    private string createdArtifactPath = string.Empty;
    private string manufacturerMailDraft = string.Empty;

    public ObservableCollection<ExcelTranslationEntryViewModel> ArtifactTranslationPreview { get; } = [];

    public AsyncRelayCommand PrepareArtifactPlanCommand { get; private set; } = null!;
    public AsyncRelayCommand CreateExcelArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand GenerateManufacturerMailCommand { get; private set; } = null!;
    public AsyncRelayCommand CancelArtifactCommand { get; private set; } = null!;
    public RelayCommand ChooseArtifactDestinationCommand { get; private set; } = null!;
    public RelayCommand ResetArtifactOutputNameCommand { get; private set; } = null!;
    public RelayCommand UseNumberedArtifactNameCommand { get; private set; } = null!;
    public RelayCommand OpenArtifactSourceCommand { get; private set; } = null!;
    public RelayCommand OpenArtifactDestinationCommand { get; private set; } = null!;
    public RelayCommand OpenCreatedArtifactCommand { get; private set; } = null!;
    public RelayCommand CopyManufacturerMailCommand { get; private set; } = null!;

    public string ArtifactStateText
    {
        get => artifactStateText;
        private set => SetProperty(ref artifactStateText, value);
    }

    public int ArtifactProgressPercent
    {
        get => artifactProgressPercent;
        private set => SetProperty(ref artifactProgressPercent, value);
    }

    public string ArtifactSourceFile
    {
        get => artifactSourceFile;
        private set => SetProperty(ref artifactSourceFile, value);
    }

    public string ArtifactDestinationFolder
    {
        get => artifactDestinationFolder;
        set
        {
            if (SetProperty(ref artifactDestinationFolder, value))
            {
                InvalidateArtifactPlan("保存先が変更されました。「実行内容を確認」で計画を再確認してください。");
                RaiseArtifactCommandStates();
            }
        }
    }

    public string ArtifactOutputFileName
    {
        get => artifactOutputFileName;
        set
        {
            if (SetProperty(ref artifactOutputFileName, value))
            {
                InvalidateArtifactPlan("出力ファイル名が変更されました。「実行内容を確認」で計画を再確認してください。");
                RaiseArtifactCommandStates();
            }
        }
    }

    public string ArtifactWarnings
    {
        get => artifactWarnings;
        private set => SetProperty(ref artifactWarnings, value);
    }

    public string ArtifactResultText
    {
        get => artifactResultText;
        private set => SetProperty(ref artifactResultText, value);
    }

    public string CreatedArtifactPath
    {
        get => createdArtifactPath;
        private set
        {
            if (SetProperty(ref createdArtifactPath, value))
            {
                RaiseArtifactCommandStates();
            }
        }
    }

    public string ManufacturerMailDraft
    {
        get => manufacturerMailDraft;
        set
        {
            if (SetProperty(ref manufacturerMailDraft, value))
            {
                RaiseArtifactCommandStates();
            }
        }
    }

    public string ArtifactSourceFullPath => artifactPlan?.SourceFullPath ?? ArtifactSourceFile;
    public string ArtifactKindText => artifactPlan is null ? "-" : "Excel英訳・別名保存";
    public string ArtifactProcessDescription => artifactPlan is null
        ? "-"
        : "日本語の文字列セル、シート名、図形内テキストを英訳し、元Excelのコピーへ反映します。数式、数値、日付、URL、画像は変更しません。";
    public string ArtifactOverwriteText => "上書きしない";
    public string ArtifactSourceProtectionText => "元ファイルは変更しない";
    public string ArtifactDestinationCreationText => artifactPlan?.DestinationFolderWillBeCreated == true
        ? "実行時に新規作成予定"
        : "既存フォルダ";
    public string ArtifactTranslationSummary => artifactPlan is null
        ? "翻訳対象は未確認です。"
        : $"Excel要素: {artifactPlan.Excel.Entries.Count} / 翻訳対象: {artifactPlan.Excel.TranslatableCount} / 対象外: {artifactPlan.Excel.UnchangedCount}";

    private void InitializeArtifactCommands()
    {
        PrepareArtifactPlanCommand = new AsyncRelayCommand(
            () => ExecuteGuardedAsync(() => PrepareArtifactPlanAsync()),
            () => !turnActive && caseFolderReady);
        CreateExcelArtifactCommand = new AsyncRelayCommand(
            () => ExecuteGuardedAsync(CreateExcelArtifactAsync),
            () => artifactPlan is not null && artifactPlanReadyForExecution && !turnActive);
        GenerateManufacturerMailCommand = new AsyncRelayCommand(
            () => ExecuteGuardedAsync(GenerateManufacturerMailAsync),
            () => artifactResult?.Succeeded == true && !turnActive);
        CancelArtifactCommand = new AsyncRelayCommand(
            CancelArtifactAsync,
            () => artifactPlan is not null || artifactTurnCompletion is not null);
        ChooseArtifactDestinationCommand = new RelayCommand(ChooseArtifactDestination, () => !turnActive);
        ResetArtifactOutputNameCommand = new RelayCommand(
            () => ArtifactOutputFileName = "Inquiry_Details_EN.xlsx",
            () => !turnActive);
        UseNumberedArtifactNameCommand = new RelayCommand(UseNumberedArtifactName, () => artifactPlan is not null && !turnActive);
        OpenArtifactSourceCommand = new RelayCommand(
            () => OpenPath(ArtifactSourceFullPath),
            () => File.Exists(ArtifactSourceFullPath));
        OpenArtifactDestinationCommand = new RelayCommand(
            () => OpenPath(ArtifactDestinationFolder),
            () => Directory.Exists(ArtifactDestinationFolder));
        OpenCreatedArtifactCommand = new RelayCommand(
            () => OpenPath(CreatedArtifactPath),
            () => File.Exists(CreatedArtifactPath));
        CopyManufacturerMailCommand = new RelayCommand(
            () => WpfClipboard.SetText(ManufacturerMailDraft),
            () => !string.IsNullOrWhiteSpace(ManufacturerMailDraft));
    }

    private async Task PrepareArtifactPlanAsync(string? instructionOverride = null)
    {
        try
        {
            await PrepareArtifactPlanCoreAsync(instructionOverride).ConfigureAwait(false);
        }
        catch
        {
            RunOnUi(() =>
            {
                ArtifactStateText = "失敗";
                ArtifactProgressPercent = 0;
                ArtifactResultText = "計画を作成できませんでした。ファイルは作成していません。";
            });
            throw;
        }
    }

    private async Task PrepareArtifactPlanCoreAsync(string? instructionOverride)
    {
        var snapshot = caseProvider();
        var instruction = string.IsNullOrWhiteSpace(instructionOverride)
            ? (string.IsNullOrWhiteSpace(PromptInput) ? artifactRequestInstruction : PromptInput.Trim())
            : instructionOverride.Trim();
        RunOnUi(() =>
        {
            ArtifactStateText = "Excel読取り中";
            ArtifactProgressPercent = 10;
            ArtifactWarnings = string.Empty;
            ArtifactResultText = string.Empty;
        });

        var source = FindArtifactSource(snapshot.CaseFolder, instruction);
        var destination = string.IsNullOrWhiteSpace(ArtifactDestinationFolder)
            ? FindDefaultArtifactDestination(snapshot)
            : ArtifactDestinationFolder;
        var outputFileName = string.IsNullOrWhiteSpace(ArtifactOutputFileName)
            ? "Inquiry_Details_EN.xlsx"
            : ArtifactOutputFileName.Trim();
        var request = new ArtifactCreationRequest
        {
            CaseFolder = snapshot.CaseFolder,
            SourceFilePath = source,
            DestinationFolder = destination,
            OutputFileName = outputFileName,
            ProductName = snapshot.ProductName,
            UserInstruction = instruction,
        };
        var plan = await excelTranslationService.CreatePlanAsync(request).ConfigureAwait(false);
        RunOnUi(() => ApplyArtifactPlan(plan, instruction));
    }

    private void ApplyArtifactPlan(ArtifactCreationPlan plan, string instruction)
    {
        artifactPlan = plan;
        artifactPlanReadyForExecution = true;
        artifactResult = null;
        artifactTranslations = [];
        artifactRequestInstruction = instruction;
        ArtifactSourceFile = plan.SourceFullPath;
        artifactDestinationFolder = plan.DestinationFullPath;
        OnPropertyChanged(nameof(ArtifactDestinationFolder));
        artifactOutputFileName = plan.Request.OutputFileName;
        OnPropertyChanged(nameof(ArtifactOutputFileName));
        CreatedArtifactPath = string.Empty;
        ManufacturerMailDraft = string.Empty;
        ArtifactTranslationPreview.Clear();
        foreach (var entry in plan.Excel.Entries)
        {
            ArtifactTranslationPreview.Add(new ExcelTranslationEntryViewModel(entry));
        }

        ArtifactWarnings = plan.Warnings.Count == 0
            ? "警告なし"
            : string.Join(Environment.NewLine, plan.Warnings.Select(static warning => $"- {warning}"));
        ArtifactResultText = $"""
            実行前です。まだファイルは作成していません。
            保存先: {plan.DestinationFullPath}
            出力予定: {plan.OutputFullPath}
            上書き: しない
            元ファイル: 変更しない
            """;
        ArtifactStateText = "ユーザー確認待ち";
        ArtifactProgressPercent = 20;
        NotifyArtifactPlanProperties();
        RaiseArtifactCommandStates();
    }

    private async Task CreateExcelArtifactAsync()
    {
        try
        {
            await CreateExcelArtifactCoreAsync().ConfigureAwait(false);
        }
        catch
        {
            RunOnUi(() =>
            {
                if (artifactResult?.Succeeded != true
                    && ArtifactStateText is not "警告あり" and not "キャンセル")
                {
                    ArtifactStateText = "失敗";
                    ArtifactProgressPercent = 0;
                    ArtifactResultText = "成果物を作成できませんでした。元ファイルは変更していません。";
                }
            });

            throw;
        }
    }

    private async Task CreateExcelArtifactCoreAsync()
    {
        if (artifactPlan is null)
        {
            throw new InvalidOperationException("先に「実行内容を確認」を押してください。");
        }

        if (!artifactPlanReadyForExecution)
        {
            throw new InvalidOperationException("保存先または出力名が変更されています。「実行内容を確認」を押してから実行してください。");
        }

        var plan = artifactPlan;
        if (File.Exists(plan.OutputFullPath))
        {
            RunOnUi(() =>
            {
                ArtifactStateText = "警告あり";
                ArtifactWarnings = "同名ファイルが存在します。出力名を編集するか「連番で保存」を押してから、実行内容を再確認してください。";
            });
            throw new IOException("同名ファイルを上書きしません。別名または連番を選択してください。");
        }

        RunOnUi(() =>
        {
            ArtifactStateText = "翻訳中";
            ArtifactProgressPercent = 35;
        });
        var context = BuildArtifactPromptContext();
        var expected = plan.Excel.Entries.Where(static item => item.ShouldTranslate).ToArray();
        var translations = new List<ExcelTranslationValue>();
        var batchNumber = 0;
        foreach (var batch in expected.Chunk(ArtifactPromptComposer.TranslationBatchSize))
        {
            batchNumber++;
            RunOnUi(() =>
                ArtifactResultText = $"Codexで翻訳中: {Math.Min(translations.Count + batch.Length, expected.Length)} / {expected.Length}セル");
            var prompt = artifactPromptComposer.ComposeTranslationPrompt(plan, batch, context);
            var response = await SendArtifactTurnAsync(
                prompt,
                $"成果物: Excel翻訳 {batchNumber}/{Math.Max(1, (int)Math.Ceiling((double)expected.Length / ArtifactPromptComposer.TranslationBatchSize))}").ConfigureAwait(false);
            var parsed = translationJsonParser.Parse(response, batch);
            if (!parsed.Succeeded)
            {
                RunOnUi(() =>
                {
                    ArtifactStateText = "警告あり";
                    ArtifactWarnings = string.Join(Environment.NewLine, parsed.Errors);
                });
                throw new InvalidDataException("Codexの翻訳JSONを安全に確認できないため、ファイルは作成していません。");
            }

            translations.AddRange(parsed.Values);
        }

        RunOnUi(() =>
        {
            ApplyTranslationPreview(translations);
            ArtifactStateText = "ファイル作成中";
            ArtifactProgressPercent = 70;
            ArtifactResultText = "一時ファイルへ翻訳を反映しています。元Excelは変更しません。";
        });
        var result = await excelTranslationService
            .CreateArtifactAsync(plan, translations)
            .ConfigureAwait(false);
        RunOnUi(() =>
        {
            artifactResult = result;
            artifactTranslations = translations;
            artifactPlanReadyForExecution = false;
            CreatedArtifactPath = result.OutputFilePath;
            ArtifactStateText = "検証中";
            ArtifactProgressPercent = 85;
            ArtifactResultText = BuildArtifactResultText(result);
        });

        try
        {
            await GenerateManufacturerMailAsync().ConfigureAwait(false);
            RunOnUi(() =>
            {
                ArtifactStateText = "完了";
                ArtifactProgressPercent = 100;
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                ArtifactStateText = "警告あり";
                ArtifactProgressPercent = 100;
                ArtifactWarnings += $"{Environment.NewLine}- Excelは保存済みですが、メーカー確認メール案を作成できませんでした: {ex.Message}";
            });
        }

        RaiseArtifactCommandStates();
    }

    private async Task GenerateManufacturerMailAsync()
    {
        if (artifactPlan is null || artifactResult?.Succeeded != true)
        {
            throw new InvalidOperationException("英訳Excelの保存成功後にメール案を作成できます。");
        }

        string[] attachmentNames = [];
        ArtifactPromptContext? promptContext = null;
        RunOnUi(() =>
        {
            ArtifactStateText = "メール案作成中";
            ArtifactProgressPercent = 90;
            attachmentNames = Files
                .Where(static file => file.IsSelected)
                .Select(static file => file.FileName)
                .Append(Path.GetFileName(artifactResult.OutputFilePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            promptContext = BuildArtifactPromptContext();
        });
        var prompt = artifactPromptComposer.ComposeManufacturerMailPrompt(
            artifactPlan,
            artifactTranslations,
            promptContext ?? throw new InvalidOperationException("案件情報を取得できませんでした。"),
            attachmentNames);
        var mailDraft = await SendArtifactTurnAsync(prompt, "成果物: メーカー向け英語メール案").ConfigureAwait(false);
        RunOnUi(() =>
        {
            ManufacturerMailDraft = mailDraft;
            ArtifactResultText = BuildArtifactResultText(artifactResult)
                + Environment.NewLine
                + "メーカー向け英語メール案を作成しました。自動送信・ファイル追記はしていません。";
            ArtifactStateText = "完了";
            ArtifactProgressPercent = 100;
        });
    }

    private async Task<string> SendArtifactTurnAsync(string prompt, string displayInstruction)
    {
        if (string.IsNullOrWhiteSpace(client.CurrentThreadId))
        {
            await StartNewAsync().ConfigureAwait(false);
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        artifactTurnCompletion = completion;
        var assistantMessage = new CodexChatMessageViewModel
        {
            Role = "assistant",
            CreatedAt = DateTimeOffset.Now,
            IsStreaming = true,
        };
        RunOnUi(() =>
        {
            Messages.Add(new CodexChatMessageViewModel
            {
                Role = "user",
                Text = displayInstruction,
                CreatedAt = DateTimeOffset.Now,
            });
            Messages.Add(assistantMessage);
            currentAssistantMessage = assistantMessage;
            turnActive = true;
            ConnectionDetails = "Codexへ成果物用の構造化データを依頼しています。ファイル書込みは行わせません。";
            RaiseCommandStates();
        });

        try
        {
            var turn = await client.StartTurnAsync(prompt).ConfigureAwait(false);
            hasSentInitialContext = true;
            if (currentSession is not null && turnActive)
            {
                currentSession = currentSession with
                {
                    LastTurnId = turn.TurnId,
                    LastUsedAt = DateTimeOffset.Now,
                    SessionStatus = "artifact-running",
                };
                await PersistSessionAsync("artifact-running").ConfigureAwait(false);
            }

            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(artifactTurnCompletion, completion))
            {
                artifactTurnCompletion = null;
            }
            RunOnUi(RaiseArtifactCommandStates);
        }
    }

    private async Task CancelArtifactAsync()
    {
        if (turnActive)
        {
            await client.InterruptTurnAsync().ConfigureAwait(false);
        }

        artifactTurnCompletion?.TrySetCanceled();
        RunOnUi(() =>
        {
            artifactPlanReadyForExecution = false;
            ArtifactStateText = "キャンセル";
            ArtifactProgressPercent = 0;
            ArtifactResultText = string.IsNullOrWhiteSpace(CreatedArtifactPath)
                ? "成果物作成をキャンセルしました。ファイルは作成していません。"
                : "後続処理をキャンセルしました。既に作成済みのファイルは削除していません。";
            RaiseArtifactCommandStates();
        });
    }

    private string FindArtifactSource(string caseFolder, string instruction)
    {
        var candidates = Files
            .Where(static file => string.Equals(Path.GetExtension(file.FullPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static file => file.IsSelected)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new FileNotFoundException("案件フォルダ内に.xlsxファイルが見つかりません。案件ファイルを再読込してください。");
        }

        var mentioned = artifactRequestDetector.FindMentionedExcelFileName(instruction);
        var selected = !string.IsNullOrWhiteSpace(mentioned)
            ? candidates.FirstOrDefault(item => string.Equals(item.FileName, mentioned, StringComparison.OrdinalIgnoreCase))
            : null;
        var explicitlySelected = candidates.Where(static item => item.IsSelected).ToArray();
        selected ??= explicitlySelected.Length == 1 ? explicitlySelected[0] : null;
        selected ??= candidates.FirstOrDefault(static item => item.FileName.Contains("問い合わせ内容", StringComparison.OrdinalIgnoreCase));
        selected ??= candidates.Length == 1 ? candidates[0] : candidates.FirstOrDefault(static item => item.IsSelected);
        return (selected ?? candidates[0]).FullPath;
    }

    private static string FindDefaultArtifactDestination(CodexCaseSnapshot snapshot)
    {
        try
        {
            var candidates = Directory
                .EnumerateDirectories(snapshot.CaseFolder, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains("メーカー連携内容", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => Path.GetFileName(path).Contains(snapshot.SupportId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(Directory.GetLastWriteTimeUtc)
                .ToArray();
            if (candidates.Length > 0)
            {
                return candidates[0];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var date = DateTime.Today.ToString("yyyyMMdd");
        return Path.Combine(snapshot.CaseFolder, $"メーカー連携内容_{date}_{snapshot.SupportId}");
    }

    private ArtifactPromptContext BuildArtifactPromptContext()
    {
        var snapshot = caseProvider();
        return new ArtifactPromptContext
        {
            ProductName = snapshot.ProductName,
            ProductPromptFilePath = snapshot.ProductPromptFilePath,
            SupportToolSettingsFilePath = snapshot.SupportToolSettingsFilePath,
            SupportId = snapshot.SupportId,
            CompanyName = snapshot.CompanyName,
            InquiryText = snapshot.InquiryText,
            UserInstruction = artifactRequestInstruction,
            CurrentCaseEvidenceReferences = BuildCurrentCaseEvidenceReferences(snapshot.Evidence),
        };
    }

    private static string BuildCurrentCaseEvidenceReferences(IReadOnlyList<SearchSource> sources)
    {
        var currentCaseSources = sources
            .Where(static source => string.Equals(source.SourceType, "CurrentCase", StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();
        if (currentCaseSources.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var source in currentCaseSources)
        {
            var excerpt = string.Join(" ", (source.Text ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (excerpt.Length > 240)
            {
                excerpt = excerpt[..240] + "...";
            }

            builder.AppendLine($"- EvidenceId: {source.SourceId}");
            builder.AppendLine($"  File: {source.Title}");
            builder.AppendLine($"  Locator: {source.Locator ?? "(unknown)"}");
            builder.AppendLine($"  Kind: {source.EvidenceKind ?? "(unknown)"}");
            builder.AppendLine($"  ContentHash: {source.ContentHash ?? "(unknown)"}");
            builder.AppendLine($"  Excerpt: {excerpt}");
        }

        return builder.ToString().TrimEnd();
    }

    private void ApplyTranslationPreview(IReadOnlyList<ExcelTranslationValue> translations)
    {
        var lookup = translations.ToDictionary(
            static item => $"{item.Sheet}\u001f{item.Cell}",
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in ArtifactTranslationPreview)
        {
            if (lookup.TryGetValue($"{item.Sheet}\u001f{item.Cell}", out var translation))
            {
                item.TranslatedText = translation.TranslatedText;
            }
        }
    }

    private void ChooseArtifactDestination()
    {
        using var dialog = new FormsFolderBrowserDialog
        {
            Description = "案件フォルダ配下の成果物保存先を選択してください。",
            InitialDirectory = Directory.Exists(ArtifactDestinationFolder)
                ? ArtifactDestinationFolder
                : caseProvider().CaseFolder,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog() == FormsDialogResult.OK)
        {
            ArtifactDestinationFolder = dialog.SelectedPath;
        }
    }

    private void UseNumberedArtifactName()
    {
        if (string.IsNullOrWhiteSpace(ArtifactDestinationFolder))
        {
            return;
        }

        ArtifactOutputFileName = artifactPathPolicy.SuggestNumberedFileName(
            ArtifactDestinationFolder,
            ArtifactOutputFileName);
    }

    private static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string BuildArtifactResultText(ArtifactCreationResult result)
    {
        return $"""
            保存完了: {result.OutputFilePath}
            翻訳対象要素数: {result.TranslationTargetCount}
            翻訳成功数: {result.TranslatedCount}
            翻訳を変更しなかった数: {result.UnchangedCount}
            警告数: {result.Warnings.Count}
            元ファイルは変更していません。
            """;
    }

    private void ResetArtifactForCaseChange()
    {
        artifactPlan = null;
        artifactPlanReadyForExecution = false;
        artifactResult = null;
        artifactTranslations = [];
        artifactRequestInstruction = string.Empty;
        ArtifactStateText = "未計画";
        ArtifactProgressPercent = 0;
        ArtifactSourceFile = string.Empty;
        artifactDestinationFolder = string.Empty;
        OnPropertyChanged(nameof(ArtifactDestinationFolder));
        artifactOutputFileName = "Inquiry_Details_EN.xlsx";
        OnPropertyChanged(nameof(ArtifactOutputFileName));
        ArtifactWarnings = string.Empty;
        ArtifactResultText = string.Empty;
        CreatedArtifactPath = string.Empty;
        ManufacturerMailDraft = string.Empty;
        ArtifactTranslationPreview.Clear();
        NotifyArtifactPlanProperties();
        RaiseArtifactCommandStates();
    }

    private void InvalidateArtifactPlan(string message)
    {
        if (artifactPlan is null)
        {
            ArtifactResultText = message;
            return;
        }

        artifactPlanReadyForExecution = false;
        ArtifactStateText = "再確認待ち";
        ArtifactProgressPercent = 0;
        ArtifactResultText = message;
    }

    private void NotifyArtifactPlanProperties()
    {
        OnPropertyChanged(nameof(ArtifactSourceFullPath));
        OnPropertyChanged(nameof(ArtifactKindText));
        OnPropertyChanged(nameof(ArtifactProcessDescription));
        OnPropertyChanged(nameof(ArtifactOverwriteText));
        OnPropertyChanged(nameof(ArtifactSourceProtectionText));
        OnPropertyChanged(nameof(ArtifactDestinationCreationText));
        OnPropertyChanged(nameof(ArtifactTranslationSummary));
    }

    private void RaiseArtifactCommandStates()
    {
        RunOnUi(() =>
        {
            PrepareArtifactPlanCommand?.RaiseCanExecuteChanged();
            CreateExcelArtifactCommand?.RaiseCanExecuteChanged();
            GenerateManufacturerMailCommand?.RaiseCanExecuteChanged();
            CancelArtifactCommand?.RaiseCanExecuteChanged();
            ChooseArtifactDestinationCommand?.RaiseCanExecuteChanged();
            ResetArtifactOutputNameCommand?.RaiseCanExecuteChanged();
            UseNumberedArtifactNameCommand?.RaiseCanExecuteChanged();
            OpenArtifactSourceCommand?.RaiseCanExecuteChanged();
            OpenArtifactDestinationCommand?.RaiseCanExecuteChanged();
            OpenCreatedArtifactCommand?.RaiseCanExecuteChanged();
            CopyManufacturerMailCommand?.RaiseCanExecuteChanged();
        });
    }
}
