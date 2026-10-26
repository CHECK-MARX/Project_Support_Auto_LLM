using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Artifacts;
using SupportCaseManager.Ai.Core.Codex;
using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Compatibility;
using SupportCaseManager.Core.Quality;
using SupportCaseManager.Core.Notes;
using FormsDialogResult = System.Windows.Forms.DialogResult;
using FormsFolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed partial class CodexChatViewModel
{
    private readonly ArtifactFilenameTranslationService artifactFilenameTranslationService = new();
    private ArtifactCreationPlan? artifactPlan;
    private ArtifactCreationResult? artifactResult;
    private IReadOnlyList<ExcelTranslationValue> artifactTranslations = [];
    private IReadOnlyList<ArtifactTextTranslationValue> artifactTextTranslations = [];
    private TaskCompletionSource<string>? artifactTurnCompletion;
    private bool artifactPlanReadyForExecution;
    private string artifactStateText = "未計画";
    private int artifactProgressPercent;
    private string artifactSourceFile = string.Empty;
    private bool artifactSourceExplicitlySelected;
    private string artifactDestinationFolder = string.Empty;
    private string artifactOutputFileName = "Inquiry_Details_EN.xlsx";
    private string artifactRequestInstruction = string.Empty;
    private string artifactWarnings = string.Empty;
    private string artifactResultText = string.Empty;
    private string manufacturerFollowUpScopeText = "メーカー確認案スコープ: 未実行";
    private string createdArtifactPath = string.Empty;
    private string pendingManufacturerAttachmentPath = string.Empty;
    private string pendingManufacturerAttachmentSourcePath = string.Empty;
    private string pendingManufacturerAttachmentSupportId = string.Empty;
    private string pendingManufacturerAttachmentCaseFolder = string.Empty;
    private string japaneseManufacturerDraft = string.Empty;
    private string englishManufacturerDraft = string.Empty;
    private ManufacturerRecipient manufacturerRecipient = new();
    private bool manufacturerMailQualityReady;
    // In-memory snapshot of the current command, never persisted to customer logs.
    public ManufacturerMailBrief? CurrentManufacturerMailBrief { get; private set; }

    public ObservableCollection<ExcelTranslationEntryViewModel> ArtifactTranslationPreview { get; } = [];
    public ObservableCollection<ArtifactTranslationPreviewItemViewModel> ArtifactPreviewItems { get; } = [];

    public AsyncRelayCommand PrepareArtifactPlanCommand { get; private set; } = null!;
    public AsyncRelayCommand CreateExcelArtifactCommand { get; private set; } = null!;
    public AsyncRelayCommand GenerateManufacturerMailCommand { get; private set; } = null!;
    public AsyncRelayCommand GenerateManufacturerReplyCommand { get; private set; } = null!;
    public AsyncRelayCommand ReviseManufacturerMailCommand { get; private set; } = null!;
    public IReadOnlyList<CodexCaseFileViewModel> ManufacturerAttachments => ResolveManufacturerAttachments(caseProvider()).Files
        .Select(file => Files.FirstOrDefault(item => string.Equals(item.FullPath, file.FullPath, StringComparison.OrdinalIgnoreCase)))
        .Where(item => item is not null).Cast<CodexCaseFileViewModel>().ToArray();
    public string ManufacturerAttachmentNotice
    {
        get
        {
            var snapshot = caseProvider();
            if (IsPendingManufacturerAttachmentForCase(snapshot)
                && !TryGetPendingManufacturerAttachment(snapshot, out _, out _, out var error)) return error;
            return string.Join(Environment.NewLine, ResolveManufacturerAttachments(snapshot).Warnings);
        }
    }
    public AsyncRelayCommand UseCreatedArtifactForManufacturerMailCommand { get; private set; } = null!;
    public AsyncRelayCommand ChooseExistingManufacturerTranslationCommand { get; private set; } = null!;
    public AsyncRelayCommand CancelArtifactCommand { get; private set; } = null!;
    public RelayCommand ChooseArtifactDestinationCommand { get; private set; } = null!;
    public RelayCommand ChooseArtifactSourceCommand { get; private set; } = null!;
    public RelayCommand ResetArtifactOutputNameCommand { get; private set; } = null!;
    public RelayCommand UseNumberedArtifactNameCommand { get; private set; } = null!;
    public RelayCommand UseDatedArtifactNameCommand { get; private set; } = null!;
    public RelayCommand OpenArtifactSourceCommand { get; private set; } = null!;
    public RelayCommand OpenArtifactDestinationCommand { get; private set; } = null!;
    public RelayCommand OpenCreatedArtifactCommand { get; private set; } = null!;
    public RelayCommand CopyJapaneseManufacturerDraftCommand { get; private set; } = null!;
    public RelayCommand CopyEnglishManufacturerDraftCommand { get; private set; } = null!;
    public AsyncRelayCommand SendEnglishManufacturerDraftToWpfNoteCommand { get; private set; } = null!;

    // Kept as a compatibility alias for saved bindings and older callers.
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
                OnPropertyChanged(nameof(ArtifactOutputPlanText));
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

    public string ManufacturerFollowUpScopeText
    {
        get => manufacturerFollowUpScopeText;
        private set => SetProperty(ref manufacturerFollowUpScopeText, value);
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

    public string JapaneseManufacturerDraft
    {
        get => japaneseManufacturerDraft;
        set
        {
            if (SetProperty(ref japaneseManufacturerDraft, value))
            {
                RaiseArtifactCommandStates();
            }
        }
    }

    public string EnglishManufacturerDraft
    {
        get => englishManufacturerDraft;
        set
        {
            if (SetProperty(ref englishManufacturerDraft, value))
            {
                RaiseArtifactCommandStates();
            }
        }
    }

    public string ManufacturerMailDraft
    {
        get => EnglishManufacturerDraft;
        set => EnglishManufacturerDraft = value;
    }

    public string ManufacturerRecipientText => $"宛先: {manufacturerRecipient.DisplayText}";

    public string PendingManufacturerAttachmentText => string.IsNullOrWhiteSpace(pendingManufacturerAttachmentPath)
        ? "次のメーカー確認メールに使用する英訳ファイル: 未指定"
        : $"次のメーカー確認メールに使用する英訳ファイル: {Path.GetFileName(pendingManufacturerAttachmentPath)}";

    public string ArtifactSourceFullPath => artifactPlan?.SourceFullPath ?? ArtifactSourceFile;
    public string ArtifactOutputPlanText
    {
        get => artifactPlan is null && string.IsNullOrWhiteSpace(ArtifactSourceFile)
            ? "NONE"
            : ArtifactOutputFileName;
        set
        {
            if (string.Equals(value?.Trim(), "NONE", StringComparison.OrdinalIgnoreCase)
                && artifactPlan is null
                && string.IsNullOrWhiteSpace(ArtifactSourceFile))
            {
                return;
            }

            ArtifactOutputFileName = value ?? string.Empty;
        }
    }
    public string ArtifactFormatText => FormatName(artifactPlan?.Format ?? CaseArtifactPathPolicy.GetArtifactFormat(ArtifactSourceFile));
    public string ArtifactKindText => artifactPlan is null
        ? (string.IsNullOrWhiteSpace(ArtifactSourceFile) ? "-" : "英訳・別名保存")
        : artifactPlan.Kind == ArtifactKind.ExcelEnglishTranslation ? "英訳・別名保存" : "英訳・同形式保存";
    public string ArtifactProcessDescription => artifactPlan is null
        ? "-"
        : artifactPlan.Format == ArtifactFormat.ExcelWorkbook
            ? "選択ファイル内の翻訳可能文字列を英訳し、元Excelを変更せず同形式で保存します。数式、数値、日付、URL、画像は変更しません。"
            : artifactPlan.Format == ArtifactFormat.PowerPointPresentation
                ? "スライド・表・ノート等の編集可能な文字を英訳し、元PPTXを変更せず同形式で保存します。配置、画像、リンク、アニメーションは保持します。画像内の文字・埋込ファイル・自動更新フィールドは変更しません。"
            : "選択ファイル内の翻訳可能文字列だけを英訳し、元ファイルを変更せず同じ形式で保存します。構造、区切り、改行、技術用語は維持します。";
    public string ArtifactOverwriteText => "上書きしない";
    public string ArtifactSourceProtectionText => "元ファイルは変更しない";
    public string ArtifactDestinationCreationText => artifactPlan?.DestinationFolderWillBeCreated == true
        ? "実行時に新規作成予定"
        : "既存フォルダ";
    public string ArtifactTranslationSummary => artifactPlan is null
        ? "翻訳対象は未確認です。"
        : artifactPlan.Format == ArtifactFormat.ExcelWorkbook
            ? $"Excel要素: {artifactPlan.Excel.Entries.Count} / 翻訳対象: {artifactPlan.Excel.TranslatableCount} / 対象外: {artifactPlan.Excel.UnchangedCount}"
            : $"{FormatName(artifactPlan.Format)}要素: {artifactPlan.Text.Entries.Count} / 翻訳対象: {artifactPlan.Text.TranslatableCount} / 対象外: {artifactPlan.Text.UnchangedCount}";

    private void InitializeArtifactCommands()
    {
        PrepareArtifactPlanCommand = new AsyncRelayCommand(
            () => ExecuteGuardedAsync(() => PrepareArtifactPlanAsync()),
            () => !turnActive && caseFolderReady);
        CreateExcelArtifactCommand = new AsyncRelayCommand(
            () => ExecuteGuardedAsync(CreateExcelArtifactAsync),
            () => artifactPlan is not null && artifactPlanReadyForExecution && !turnActive);
        GenerateManufacturerMailCommand = new AsyncRelayCommand(
            () => ExecuteGuardedAsync(() => BeginManufacturerMailAsync(ManufacturerCommunicationIntent.AskManufacturer)),
            () => caseFolderReady && !turnActive);
        GenerateManufacturerReplyCommand = new AsyncRelayCommand(
            () => ExecuteGuardedAsync(() => BeginManufacturerMailAsync(ManufacturerCommunicationIntent.ReplyToManufacturer)),
            () => caseFolderReady && !turnActive);
        ReviseManufacturerMailCommand = new AsyncRelayCommand(
            () => ExecuteGuardedAsync(() => GenerateManufacturerMailAsync(PromptInput,
                lastManufacturerIntent == "REPLY_TO_MANUFACTURER" ? ManufacturerCommunicationIntent.ReplyToManufacturer : ManufacturerCommunicationIntent.AskManufacturer)),
            () => !turnActive && caseFolderReady && !string.IsNullOrWhiteSpace(PromptInput)
                && (!string.IsNullOrWhiteSpace(JapaneseManufacturerDraft) || !string.IsNullOrWhiteSpace(EnglishManufacturerDraft)));
        UseCreatedArtifactForManufacturerMailCommand = new AsyncRelayCommand(
            UseCreatedArtifactForManufacturerMail,
            CanUseCreatedArtifactForManufacturerMail);
        ChooseExistingManufacturerTranslationCommand = new AsyncRelayCommand(
            () => ExecuteGuardedAsync(ChooseExistingManufacturerTranslationAsync), () => !turnActive && caseFolderReady);
        CancelArtifactCommand = new AsyncRelayCommand(
            CancelArtifactAsync,
            () => artifactPlan is not null || artifactTurnCompletion is not null);
        ChooseArtifactDestinationCommand = new RelayCommand(ChooseArtifactDestination, () => !turnActive);
        ChooseArtifactSourceCommand = new RelayCommand(ChooseArtifactSource, () => !turnActive && caseFolderReady);
        ResetArtifactOutputNameCommand = new RelayCommand(
            () => ArtifactOutputFileName = string.IsNullOrWhiteSpace(ArtifactSourceFile)
                ? "Inquiry_Details_EN.xlsx"
                : artifactFilenameTranslationService.CreatePreview(ArtifactSourceFile).OutputFileName,
            () => !turnActive);
        UseNumberedArtifactNameCommand = new RelayCommand(UseNumberedArtifactName, () => artifactPlan is not null && !turnActive);
        UseDatedArtifactNameCommand = new RelayCommand(
            UseDatedArtifactName,
            () => artifactPlan is not null && !turnActive);
        OpenArtifactSourceCommand = new RelayCommand(
            () => OpenPath(ArtifactSourceFullPath),
            () => File.Exists(ArtifactSourceFullPath));
        OpenArtifactDestinationCommand = new RelayCommand(
            () => OpenPath(ArtifactDestinationFolder),
            () => Directory.Exists(ArtifactDestinationFolder));
        OpenCreatedArtifactCommand = new RelayCommand(
            () => OpenPath(CreatedArtifactPath),
            () => File.Exists(CreatedArtifactPath));
        CopyJapaneseManufacturerDraftCommand = new RelayCommand(
            () => CopyManufacturerDraft(JapaneseManufacturerDraft),
            () => !string.IsNullOrWhiteSpace(JapaneseManufacturerDraft));
        CopyEnglishManufacturerDraftCommand = new RelayCommand(
            () => CopyManufacturerDraft(EnglishManufacturerDraft),
            () => !string.IsNullOrWhiteSpace(EnglishManufacturerDraft));
        SendEnglishManufacturerDraftToWpfNoteCommand = new AsyncRelayCommand(
            SendEnglishManufacturerDraftToWpfNoteAsync,
            CanSendEnglishManufacturerDraftToWpfNote);
        CopyManufacturerMailCommand = new RelayCommand(
            () => CopyManufacturerDraft(EnglishManufacturerDraft),
            () => !string.IsNullOrWhiteSpace(EnglishManufacturerDraft));
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
            ArtifactStateText = "元ファイル読取り中";
            ArtifactProgressPercent = 10;
            ArtifactWarnings = string.Empty;
            ArtifactResultText = string.Empty;
        });

        var currentDeltaSource = FindCurrentCustomerDeltaSource(snapshot);
        var sourceWasAlreadySelected = !string.IsNullOrWhiteSpace(ArtifactSourceFile);
        var source = currentDeltaSource is not null
                && !artifactSourceExplicitlySelected
                && !string.Equals(currentDeltaSource, ArtifactSourceFile, StringComparison.OrdinalIgnoreCase)
            ? currentDeltaSource
            : !sourceWasAlreadySelected
                ? FindArtifactSource(snapshot.CaseFolder, instruction)
                : ArtifactSourceFile;
        var destination = string.IsNullOrWhiteSpace(ArtifactDestinationFolder)
            ? FindDefaultArtifactDestination(snapshot)
            : ArtifactDestinationFolder;
        var outputFileName = !sourceWasAlreadySelected
            || !string.Equals(source, ArtifactSourceFile, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(ArtifactOutputFileName)
            ? artifactFilenameTranslationService.CreatePreview(source).OutputFileName
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
        var format = CaseArtifactPathPolicy.GetArtifactFormat(source);
        var plan = format == ArtifactFormat.ExcelWorkbook
            ? await excelTranslationService.CreatePlanAsync(request).ConfigureAwait(false)
            : await artifactTranslationService.CreatePlanAsync(request).ConfigureAwait(false);
        RunOnUi(() => ApplyArtifactPlan(plan, instruction));
    }

    private void ApplyArtifactPlan(ArtifactCreationPlan plan, string instruction)
    {
        artifactPlan = plan;
        artifactPlanReadyForExecution = true;
        artifactResult = null;
        artifactTranslations = [];
        artifactTextTranslations = [];
        artifactRequestInstruction = instruction;
        ArtifactSourceFile = plan.SourceFullPath;
        artifactDestinationFolder = plan.DestinationFullPath;
        OnPropertyChanged(nameof(ArtifactDestinationFolder));
        artifactOutputFileName = plan.Request.OutputFileName;
        OnPropertyChanged(nameof(ArtifactOutputFileName));
        CreatedArtifactPath = string.Empty;
        ClearManufacturerDrafts();
        SetTranslationOnlyRuntimeDiagnostic(plan);
        WarningText = string.Empty;
        ErrorText = string.Empty;
        ArtifactTranslationPreview.Clear();
        ArtifactPreviewItems.Clear();
        foreach (var entry in plan.Excel.Entries)
        {
            ArtifactTranslationPreview.Add(new ExcelTranslationEntryViewModel(entry));
            ArtifactPreviewItems.Add(ArtifactTranslationPreviewItemViewModel.FromExcel(entry));
        }

        foreach (var entry in plan.Text.Entries)
        {
            ArtifactPreviewItems.Add(ArtifactTranslationPreviewItemViewModel.FromText(entry));
        }

        var filenameWarning = artifactFilenameTranslationService.CreatePreview(plan.SourceFullPath).Warning;
        var warnings = plan.Warnings
            .Prepend(filenameWarning)
            .Where(static warning => !string.IsNullOrWhiteSpace(warning))
            .ToArray();
        ArtifactWarnings = warnings.Length == 0
            ? "警告なし"
            : string.Join(Environment.NewLine, warnings.Select(static warning => $"- {warning}"));
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
        if (plan.Format != ArtifactFormat.ExcelWorkbook)
        {
            await CreateTextArtifactCoreAsync(plan).ConfigureAwait(false);
            return;
        }

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
            var values = await TranslateArtifactBatchAsync<ExcelTranslationValue>(
                prompt,
                $"成果物: Excel翻訳 {batchNumber}/{Math.Max(1, (int)Math.Ceiling((double)expected.Length / ArtifactPromptComposer.TranslationBatchSize))}",
                response =>
                {
                    var parsed = translationJsonParser.ParseIdentified(response, batch);
                    return (parsed.Succeeded, parsed.Values, parsed.Errors);
                }).ConfigureAwait(false);
            translations.AddRange(values);
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

        RunOnUi(() =>
        {
            ArtifactStateText = "完了";
            ArtifactProgressPercent = 100;
        });

        RaiseArtifactCommandStates();
    }

    private async Task CreateTextArtifactCoreAsync(ArtifactCreationPlan plan)
    {
        var context = BuildArtifactPromptContext();
        var expected = plan.Text.Entries.Where(static item => item.ShouldTranslate).ToArray();
        var translations = new List<ArtifactTextTranslationValue>();
        var batchNumber = 0;
        foreach (var batch in expected.Chunk(ArtifactPromptComposer.TranslationBatchSize))
        {
            batchNumber++;
            RunOnUi(() =>
                ArtifactResultText = $"Codexで翻訳中: {Math.Min(translations.Count + batch.Length, expected.Length)} / {expected.Length}要素");
            var prompt = artifactPromptComposer.ComposeTextTranslationPrompt(plan, batch, context);
            var values = await TranslateArtifactBatchAsync<ArtifactTextTranslationValue>(
                prompt,
                $"成果物: {FormatName(plan.Format)}翻訳 {batchNumber}/{Math.Max(1, (int)Math.Ceiling((double)expected.Length / ArtifactPromptComposer.TranslationBatchSize))}",
                response =>
                {
                    var parsed = artifactTextTranslationJsonParser.ParseIdentified(response, batch);
                    return (parsed.Succeeded, parsed.Values, parsed.Errors);
                }).ConfigureAwait(false);
            translations.AddRange(values);
        }

        RunOnUi(() =>
        {
            ApplyTextTranslationPreview(translations);
            ArtifactStateText = "ファイル作成中";
            ArtifactProgressPercent = 70;
            ArtifactResultText = "一時ファイルへ翻訳を反映しています。元ファイルは変更しません。";
        });
        var result = await artifactTranslationService
            .CreateArtifactAsync(plan, translations)
            .ConfigureAwait(false);
        RunOnUi(() =>
        {
            artifactResult = result;
            artifactTextTranslations = translations;
            artifactPlanReadyForExecution = false;
            CreatedArtifactPath = result.OutputFilePath;
            ArtifactStateText = "検証中";
            ArtifactProgressPercent = 85;
            ArtifactResultText = BuildArtifactResultText(result);
        });

        RunOnUi(() =>
        {
            ArtifactStateText = "完了";
            ArtifactProgressPercent = 100;
        });

        RaiseArtifactCommandStates();
    }

    private async Task<IReadOnlyList<T>> TranslateArtifactBatchAsync<T>(
        string prompt,
        string displayInstruction,
        Func<string, (bool Succeeded, IReadOnlyList<T> Values, IReadOnlyList<string> Errors)> validate)
    {
        // Both attempts use the complete, unchanged batch validation before any file is written.
        var currentPrompt = prompt;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await SendArtifactTurnAsync(
                currentPrompt, displayInstruction, showResponseInChat: false).ConfigureAwait(false);
            var parsed = validate(response);
            if (parsed.Succeeded)
            {
                RunOnUi(() => ArtifactStateText = "翻訳中");
                return parsed.Values;
            }

            // Do not log source text, translated text, or customer identifiers.
            await logger.WriteAsync("artifact-validation",
                $"Translation batch rejected; attempt={attempt + 1}; errorCount={parsed.Errors.Count}").ConfigureAwait(false);
            if (attempt == 0)
            {
                RunOnUi(() =>
                {
                    ArtifactStateText = "翻訳結果再確認中";
                    ArtifactResultText = "翻訳結果に不一致があったため、1回だけ修正を依頼しています。";
                });
                currentPrompt = ArtifactPromptComposer.ComposeTranslationRepairPrompt(prompt, parsed.Errors);
                continue;
            }

            RunOnUi(() =>
            {
                ArtifactStateText = "警告あり";
                ArtifactWarnings = string.Join(Environment.NewLine, parsed.Errors);
            });
            throw new InvalidDataException(
                "修正後も翻訳結果が入力と一致しないため、ファイルは作成していません。" + Environment.NewLine
                + string.Join(Environment.NewLine, parsed.Errors.Take(3)));
        }

        throw new InvalidOperationException("翻訳結果の検証が完了していません。");
    }

    private Task BeginManufacturerMailAsync(ManufacturerCommunicationIntent intent)
    {
        SelectedPreset = PromptPresets.Single(preset => preset.CanonicalKey ==
            (intent == ManufacturerCommunicationIntent.AskManufacturer ? CodexPromptPreset.AskManufacturerKey : CodexPromptPreset.ReplyToManufacturerKey));
        RecordGuiSendRoute("MANUFACTURER", intent == ManufacturerCommunicationIntent.AskManufacturer ? "ASK_MANUFACTURER" : "REPLY_TO_MANUFACTURER");
        return GenerateManufacturerMailAsync(SelectedPreset.Prompt, intent);
    }

    private async Task GenerateManufacturerMailAsync(
        string? instructionOverride = null,
        ManufacturerCommunicationIntent? intentOverride = null)
    {
        var snapshot = caseProvider();
        currentSnapshot = snapshot;
        await RefreshFilesAsync(preserveSelection: true).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(instructionOverride))
        {
            artifactRequestInstruction = instructionOverride.Trim();
        }

        var intent = intentOverride ?? (IsManufacturerReplyRequest(instructionOverride ?? string.Empty)
            ? ManufacturerCommunicationIntent.ReplyToManufacturer
            : ManufacturerCommunicationIntent.AskManufacturer);
        if (intent == ManufacturerCommunicationIntent.AskManufacturer
            && IsPendingManufacturerAttachmentForCase(snapshot)
            && !TryGetPendingManufacturerAttachment(snapshot, out _, out _, out var pendingAttachmentError))
        {
            RunOnUi(() =>
            {
                ArtifactStateText = "警告あり";
                ArtifactWarnings = pendingAttachmentError;
                ErrorText = pendingAttachmentError;
            });
            return;
        }
        if (intent == ManufacturerCommunicationIntent.AskManufacturer
            && artifactResult is not null
            && !string.IsNullOrWhiteSpace(CreatedArtifactPath)
            && !IsPendingManufacturerAttachmentForCase(snapshot))
        {
            RunOnUi(() =>
            {
                ArtifactStateText = "警告あり";
                ArtifactWarnings = "TRANSLATED_ARTIFACT_NOT_SELECTED: 作成した英訳ファイルをメーカー確認メールに添付するには、「メーカー確認メールで使用」を選択してください。";
                ErrorText = ArtifactWarnings;
            });
            return;
        }
        if (intent == ManufacturerCommunicationIntent.ReplyToManufacturer)
        {
            ClearUnrequestedArtifactTranslationPlan();
            ClearStaleManufacturerTechnicalAnswer();
        }

        var workbookText = intent == ManufacturerCommunicationIntent.AskManufacturer
            ? await ReadManufacturerInquiryWorkbookTextAsync(snapshot).ConfigureAwait(false) : [];
        var context = BuildManufacturerMailCaseContext(snapshot, intent, workbookText);
        if (intent == ManufacturerCommunicationIntent.ReplyToManufacturer
            && !string.IsNullOrWhiteSpace(context.ImmediateManufacturerRecipientName))
        {
            manufacturerRecipient = new ManufacturerRecipient
            {
                DisplayName = context.ImmediateManufacturerRecipientName,
                ResolutionStatus = "RESOLVED",
            };
            RunOnUi(() => OnPropertyChanged(nameof(ManufacturerRecipientText)));
        }
        var prompt = AppendQualityStylePrompt(
            artifactPromptComposer.ComposeSimpleBilingualManufacturerMailPrompt(context), snapshot,
            QualityAudience.Manufacturer,
            intent == ManufacturerCommunicationIntent.ReplyToManufacturer ? "MANUFACTURER_REPLY" : "MANUFACTURER_ASK");
        if (!string.IsNullOrWhiteSpace(instructionOverride)
            && instructionOverride != CodexPromptPreset.ManufacturerConfirmationPrompt
            && instructionOverride != CodexPromptPreset.ManufacturerReplyPrompt)
        {
            prompt += $"\n### 今回のメール修正依頼\n{instructionOverride}\n"
                + "修正先は日英メーカー向けメール案。技術回答案は変更しない。今回の確定Contextを優先し、"
                + "旧案に根拠のない経緯・添付・技術事実があれば引き継がない。\n"
                + System.Text.Json.JsonSerializer.Serialize(new ManufacturerDraftPair
                { JapaneseDraft = JapaneseManufacturerDraft, EnglishDraft = EnglishManufacturerDraft });
        }
        prompt = ArtifactPromptComposer.AppendManufacturerOutputContract(prompt, context);
        var promptParity = context.ProtectedValues.EvaluatePromptInjection(prompt);
        var runtimeDiagnostic = BuildManufacturerRuntimeDiagnostic(context, promptParity);
        RunOnUi(() =>
        {
            manufacturerMailQualityReady = false;
            CurrentManufacturerMailBrief = null;
            ArtifactStateText = "メール案作成中";
            ArtifactProgressPercent = 90;
            ManufacturerFollowUpScopeText = ManufacturerFollowUpScopeText.StartsWith(
                "Artifact Translation Runtime:",
                StringComparison.Ordinal)
                ? runtimeDiagnostic
                : $"{ManufacturerFollowUpScopeText}{Environment.NewLine}{runtimeDiagnostic}";
        });
        if (!context.CanGenerateMail)
        {
            RunOnUi(() =>
            {
                ArtifactStateText = "警告あり";
                ArtifactWarnings = intent == ManufacturerCommunicationIntent.ReplyToManufacturer
                    ? "MANUFACTURER_RESPONSE_INCOMPLETE: 直近のメーカー回答を確認できません。回答本文をチャットへ貼り付けてから、メーカー回答への返信を実行してください。"
                    : "MAIL_CONTEXT_INCOMPLETE: 現在の追加問い合わせ、今回の添付、またはメーカーFollow-up許可状態を確認できません。メールは生成していません。";
                ErrorText = ArtifactWarnings;
            });
            return;
        }

        if (!promptParity.PromptParity)
        {
            RunOnUi(() =>
            {
                ArtifactStateText = "警告あり";
                ArtifactWarnings = "PROTECTED_VALUE_INJECTION_INCOMPLETE: Canonical Protected Value Setを完全に送信プロンプトへ注入できません。メールは生成していません。";
                ErrorText = ArtifactWarnings;
                ConnectionDetails = "Protected Valueの送信前検証に失敗したため、メール案の生成を中止しました。";
                RaiseArtifactCommandStates();
            });
            return;
        }

        var generationPrompt = prompt;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var technicalAnswerBeforeManufacturerTurn = TechnicalAnswer;
                var response = await SendArtifactTurnAsync(
                    generationPrompt,
                    intent == ManufacturerCommunicationIntent.ReplyToManufacturer
                        ? "成果物: メーカー回答への返信案（日英）"
                        : "成果物: メーカーへの確認・追加質問メール案（日英）").ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(technicalAnswerBeforeManufacturerTurn)
                    && string.IsNullOrWhiteSpace(TechnicalAnswer))
                {
                    RunOnUi(() => TechnicalAnswer = technicalAnswerBeforeManufacturerTurn);
                }
                var parsed = manufacturerDraftPairParser.Parse(response);
                if (!parsed.Succeeded || parsed.Pair is null)
                {
                    RunOnUi(() =>
                    {
                        ArtifactStateText = "警告あり";
                        ArtifactWarnings = string.Join(Environment.NewLine, parsed.Errors);
                    });
                    throw new InvalidDataException("Codexの日英メーカー確認案を安全に確認できませんでした。");
                }

                var senderNormalizedPair = ApplySenderProfile(parsed.Pair);
                var boundaryContext = new ManufacturerDraftBoundaryContext
                {
                    CustomerPersonName = snapshot.CustomerName,
                    CustomerCompanyName = snapshot.CompanyName,
                    ProhibitedAttachmentNames = Files
                        .Where(file => IsGeneratedTranslationFile(file.FileName)
                            || (IsPendingManufacturerAttachmentForCase(snapshot)
                                && string.Equals(file.FullPath, pendingManufacturerAttachmentSourcePath, StringComparison.OrdinalIgnoreCase)))
                        .Select(static file => file.FileName)
                        .Where(name => !context.OutboundAttachments.Contains(name, StringComparer.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                };
                await SaveQualityDraftSafelyAsync(snapshot, QualityAudience.Manufacturer,
                    intent == ManufacturerCommunicationIntent.ReplyToManufacturer ? "MANUFACTURER_REPLY" : "MANUFACTURER_ASK",
                    senderNormalizedPair.EnglishDraft).ConfigureAwait(false);
                RunOnUi(() =>
                {
                    JapaneseManufacturerDraft = senderNormalizedPair.JapaneseDraft;
                    EnglishManufacturerDraft = senderNormalizedPair.EnglishDraft;
                    if (QualityReviewEnabled)
                        ManufacturerFollowUpScopeText += $"{Environment.NewLine}Quality Review: {QualityStyleReviewer.Review(senderNormalizedPair.EnglishDraft, instructionOverride, qualityLastRetrievalCount)}";
                    lastJapaneseManufacturerDraftAssigned = !string.IsNullOrWhiteSpace(JapaneseManufacturerDraft);
                    lastEnglishManufacturerDraftAssigned = !string.IsNullOrWhiteSpace(EnglishManufacturerDraft);
                    lastTechnicalAnswerChanged = !string.Equals(
                        technicalAnswerBeforeManufacturerTurn,
                        TechnicalAnswer,
                        StringComparison.Ordinal);
                });
                AppendManufacturerSendDiagnostic();
                var closeIntentDetected = !context.CloseRequested
                    && (ManufacturerMailConcepts.HasCloseRequest(senderNormalizedPair.JapaneseDraft)
                        || ManufacturerMailConcepts.HasCloseRequest(senderNormalizedPair.EnglishDraft));
                if (closeIntentDetected)
                {
                    RunOnUi(() =>
                    {
                        manufacturerMailQualityReady = false;
                        ArtifactStateText = "警告あり";
                        ArtifactWarnings = "UNSUPPORTED_CLOSE_INTENT: CloseRequested=falseですが、生成案に案件終了の意図が含まれています。WPFノート編集への転送は無効です。";
                        ErrorText = ArtifactWarnings;
                        ConnectionDetails = "メール案を表示しましたが、終了意図の安全検証に失敗したためWPFノート編集への転送は無効です。";
                        RaiseArtifactCommandStates();
                    });
                    return;
                }

                var unexpectedQuestionDetected = context.CommunicationIntent == ManufacturerCommunicationIntent.ReplyToManufacturer
                    && (ManufacturerMailConcepts.HasQuestionRequest(senderNormalizedPair.JapaneseDraft)
                        || ManufacturerMailConcepts.HasQuestionRequest(senderNormalizedPair.EnglishDraft));
                if (unexpectedQuestionDetected)
                {
                    RunOnUi(() =>
                    {
                        manufacturerMailQualityReady = false;
                        ArtifactStateText = "警告あり";
                        ArtifactWarnings = "UNEXPECTED_QUESTION_GENERATION: メーカー回答への返信に、明示されていない追加質問または確認依頼が含まれています。WPFノート編集への転送は無効です。";
                        ErrorText = ArtifactWarnings;
                        ConnectionDetails = "メール案を表示しましたが、返信Intentの質問ガードに失敗したためWPFノート編集への転送は無効です。";
                        RaiseArtifactCommandStates();
                    });
                    return;
                }

                var validation = manufacturerDraftPairParser.Validate(
                    senderNormalizedPair,
                    context.ProtectedValues,
                    context.OutboundAttachments,
                    boundaryContext);
                var communicationErrors = manufacturerDraftPairParser.ValidateCommunication(senderNormalizedPair, context);
                if (communicationErrors.Count > 0)
                    validation = validation with { Succeeded = false, Errors = validation.Errors.Concat(communicationErrors).ToArray() };
                var draftParity = context.ProtectedValues.EvaluateDraftParity(prompt, validation);
                if (!validation.Succeeded || !draftParity.IsComplete)
                {
                    var validationMessage = BuildManufacturerValidationMessage(validation, draftParity);
                    if (attempt == 0)
                    {
                        generationPrompt = ArtifactPromptComposer.ComposeManufacturerRepairPrompt(prompt,
                            senderNormalizedPair, [validationMessage]);
                        RunOnUi(() =>
                        {
                            if (currentAssistantMessage is not null)
                                currentAssistantMessage.Text = "日英案に不一致があり、1回だけ修正を依頼しています。";
                            ArtifactStateText = "メール案再確認中";
                        });
                        continue;
                    }
                    RunOnUi(() =>
                    {
                        ArtifactStateText = "警告あり";
                        ArtifactWarnings = validationMessage;
                        ErrorText = validationMessage;
                        ConnectionDetails = "日英メーカー確認案を確定していません。整合性チェック結果を確認してください。";
                        ManufacturerFollowUpScopeText += $"{Environment.NewLine}{BuildProtectedValueDiagnostic(draftParity)}";
                        RaiseArtifactCommandStates();
                    });
                    return;
                }

                RunOnUi(() =>
                {
                    JapaneseManufacturerDraft = senderNormalizedPair.JapaneseDraft;
                    EnglishManufacturerDraft = senderNormalizedPair.EnglishDraft;
                    manufacturerMailQualityReady = true;
                    ArtifactResultText = context.CommunicationIntent == ManufacturerCommunicationIntent.ReplyToManufacturer
                        ? "メーカー回答への返信案（日英）を作成しました。自動送信・ファイル追記はしていません。翻訳成果物計画: NONE"
                        : "メーカーへの確認・追加質問メール案（日英）を作成しました。自動送信・ファイル追記はしていません。";
                    ArtifactStateText = "完了";
                    ArtifactProgressPercent = 100;
                    RaiseArtifactCommandStates();
                });
                return;
            }
        }
        finally
        {
            RunOnUi(() =>
            {
                SetManufacturerDraftStatusMessage(manufacturerMailQualityReady
                    ? "日英メーカー確認案の整合性を確認しました。下の編集欄を確認してください。"
                    : "日英メーカー確認案は未確定です。検証結果を確認してください。");
            });
        }
    }

    private void SetManufacturerDraftStatusMessage(string text)
    {
        // The streaming reference is cleared on turn completion, before draft validation.
        var message = Messages.LastOrDefault(item => item.Role == "assistant"
            && item.Text == "日英メーカー確認案を受信しました。整合性を確認しています。");
        if (message is not null) message.Text = text;
    }

    private static string BuildManufacturerValidationMessage(
        ManufacturerDraftValidationResult validation,
        ManufacturerProtectedValueParity parity)
    {
        static string Status(bool value) => value ? "PASS" : "FAIL";
        static string Values(IReadOnlyList<string> values) => values.Count == 0
            ? "なし"
            : string.Join(", ", values);

        return $"日英メーカー確認案の整合性チェックに失敗しました。{Environment.NewLine}"
            + $"質問番号: {Status(validation.QuestionParity)}{Environment.NewLine}"
            + $"添付ファイル名: {Status(validation.AttachmentParity)}{Environment.NewLine}"
            + $"技術値: {Status(validation.TechnicalParity)}{Environment.NewLine}"
            + $"Protected Value Parity: {(parity.IsComplete ? "PASS" : "FAIL")}{Environment.NewLine}"
            + $"データ境界: {Status(validation.DataBoundary)}{Environment.NewLine}"
            + $"日本語案で不足: {Values(validation.MissingJapaneseProtectedValues)}{Environment.NewLine}"
            + $"英語案で不足: {Values(validation.MissingEnglishProtectedValues)}{Environment.NewLine}"
            + $"境界違反: {FormatBoundaryViolations(validation.DataBoundaryViolations)}{Environment.NewLine}"
            + string.Join(Environment.NewLine, validation.Errors);
    }

    private static ManufacturerDraftPair ApplySenderProfile(ManufacturerDraftPair pair) => pair with
    {
        JapaneseDraft = ReplaceTrailingSignature(
            pair.JapaneseDraft,
            ["株式会社東陽テクニカ", "東陽テクニカ", "Toyo Technica", "TOYO Support Team", "Support Team", "Support Desk", "CxOne Support"],
            "東陽テクニカ\n伊藤 健"),
        EnglishDraft = ReplaceTrailingSignature(
            pair.EnglishDraft,
            ["Best regards", "Regards", "Sincerely", "Ken Ito", "Toyo Corporation", "Toyo Technica", "TOYO Support Team", "Support Team", "Support Desk", "CxOne Support"],
            "Best regards,\nKen Ito\nToyo Corporation"),
    };

    private static string ReplaceTrailingSignature(
        string draft,
        IReadOnlyList<string> signatureMarkers,
        string senderProfile)
    {
        var lines = draft
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var tailStart = Math.Max(0, lines.Count - 8);
        var signatureStart = -1;
        for (var index = lines.Count - 1; index >= tailStart; index--)
        {
            if (!lines[index].StartsWith("Hello ", StringComparison.OrdinalIgnoreCase)
                && IsSignatureLine(lines[index], signatureMarkers))
            {
                signatureStart = index;
            }
        }

        var body = signatureStart >= 0
            ? string.Join(Environment.NewLine, lines.Take(signatureStart)).TrimEnd()
            : string.Join(Environment.NewLine, lines).TrimEnd();
        return string.IsNullOrWhiteSpace(body)
            ? senderProfile
            : $"{body}{Environment.NewLine}{Environment.NewLine}{senderProfile}";

        static bool IsSignatureLine(string line, IReadOnlyList<string> markers)
        {
            var normalized = line.Trim().TrimEnd(',', '.', '、', '。', ':');
            return markers.Any(marker => marker is "Best regards" or "Regards" or "Sincerely"
                ? normalized.StartsWith(marker, StringComparison.OrdinalIgnoreCase)
                : string.Equals(normalized, marker, StringComparison.OrdinalIgnoreCase));
        }
    }

    private string BuildManufacturerRuntimeDiagnostic(
        ManufacturerMailCaseContext context,
        ManufacturerProtectedValueParity parity)
    {
        var hasTranslationPlan = context.CommunicationIntent != ManufacturerCommunicationIntent.ReplyToManufacturer
            && (artifactPlan is not null || !string.IsNullOrWhiteSpace(ArtifactSourceFile));
        var artifactSource = hasTranslationPlan
            ? BaseName(artifactPlan?.SourceFullPath ?? ArtifactSourceFile)
            : string.Empty;
        var artifactOutput = hasTranslationPlan
            ? BaseName(artifactPlan?.Request.OutputFileName ?? ArtifactOutputFileName)
            : string.Empty;
        var selectedSource = hasTranslationPlan ? BaseName(ArtifactSourceFile) : string.Empty;

        return string.Join(
            Environment.NewLine,
            "Runtime Manufacturer Mail Context:",
            $"Runtime Case: {ValueOrUnavailable(context.SupportId)}",
            $"Current Customer Delta: {ValueOrUnavailable(context.CurrentCustomerDeltaFileName)}",
            $"Current Customer Delta Source Type: {context.CurrentCustomerDeltaSourceType}",
            $"Selected Translation Source: {ValueOrUnavailable(selectedSource)}",
            $"Auto-Selected Translation Source: {ValueOrUnavailable(context.CurrentCustomerDeltaFileName)}",
            $"Artifact Source: {ValueOrUnavailable(artifactSource)}",
            $"Artifact Output: {ValueOrUnavailable(artifactOutput)}",
            $"Current Outbound Attachment: {ValueOrUnavailable(context.CurrentOutboundAttachment)}",
            $"Current Outbound Attachments: {string.Join(", ", context.OutboundAttachments)}",
            $"Attachment Notice: {string.Join(" / ", context.AttachmentWarnings)}",
            $"Manufacturer Communication Intent: {context.CommunicationIntent}",
            $"Manufacturer Draft Mode: {(context.CommunicationIntent == ManufacturerCommunicationIntent.ReplyToManufacturer ? "REPLY" : context.IsFollowUp ? "FOLLOW_UP" : "INITIAL")}",
            $"Mail Mode: {(context.CommunicationIntent == ManufacturerCommunicationIntent.ReplyToManufacturer ? "ACKNOWLEDGEMENT" : context.IsFollowUp ? "ATTACHMENT_CENTRIC_FOLLOWUP" : "INITIAL_INQUIRY")}",
            $"Customer Reply Allowed: {(context.CustomerReplyAllowed ? "YES" : "NO")}",
            $"Manufacturer Follow-up Allowed: {(context.ManufacturerFollowupAllowed ? "YES" : "NO")}",
            $"Close Requested: {(context.CloseRequested ? "TRUE" : "FALSE")}",
            $"Customer Intent Contains Close: {(context.CloseRequested ? "YES" : "NO")}",
            "Forbidden Close Intent: ENABLED",
            BuildProtectedValueDiagnostic(parity),
            $"Protected Value Provenance: {BuildProtectedValueProvenance(context.ProtectedValues)}",
            $"Previous Manufacturer Contact: {(context.PreviousManufacturerContact ? "CONFIRMED" : "NOT_CONFIRMED")}",
            $"Previous Customer Reply: {(context.PreviousCustomerReply ? "FOUND" : "NOT_FOUND")}",
            "Prior Manufacturer Response Content: NOT_AVAILABLE");

        static string BaseName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileName(value.Trim());
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        static string ValueOrUnavailable(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "NOT_AVAILABLE" : value;
    }

    private void AppendManufacturerSendDiagnostic()
    {
        RunOnUi(() =>
        {
            ManufacturerFollowUpScopeText += string.Join(
                Environment.NewLine,
                string.Empty,
                "GUI Send Runtime:",
                $"GUI Send Route: {lastSendRoute}",
                $"Manufacturer Intent: {lastManufacturerIntent}",
                $"JP Draft Assigned: {(lastJapaneseManufacturerDraftAssigned ? "YES" : "NO")}",
                $"EN Draft Assigned: {(lastEnglishManufacturerDraftAssigned ? "YES" : "NO")}",
                $"TechnicalAnswer Changed: {(lastTechnicalAnswerChanged ? "YES" : "NO")}");
        });
    }

    private static string BuildProtectedValueDiagnostic(ManufacturerProtectedValueParity parity) =>
        $"Protected Values Expected: {parity.ExpectedCount}{Environment.NewLine}"
        + $"Protected Values Injected: {parity.InjectedCount}{Environment.NewLine}"
        + $"Protected Values Missing: {parity.MissingCount}{Environment.NewLine}"
        + $"Protected Value Parity: {(parity.IsComplete ? "PASS" : "FAIL")}";

    private static string BuildProtectedValueProvenance(ManufacturerProtectedValueSet protectedValues)
    {
        var sources = protectedValues.Items
            .Select(static item => item.Source)
            .Where(static source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static source => source, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return sources.Length == 0 ? "NONE" : string.Join(", ", sources);
    }

    private void SetTranslationOnlyRuntimeDiagnostic(ArtifactCreationPlan plan)
    {
        CurrentManufacturerMailBrief = null;
        ManufacturerFollowUpScopeText = string.Join(
            Environment.NewLine,
            "Artifact Translation Runtime:",
            "Current Operation: ARTIFACT_TRANSLATION",
            $"Runtime Case: {ValueOrNotApplicable(caseProvider().SupportId)}",
            "Current Customer Delta: NOT_APPLICABLE",
            $"Selected Translation Source: {ValueOrNotApplicable(Path.GetFileName(plan.SourceFullPath))}",
            $"Artifact Source: {ValueOrNotApplicable(Path.GetFileName(plan.SourceFullPath))}",
            $"Artifact Output: {ValueOrNotApplicable(Path.GetFileName(plan.OutputFullPath))}",
            "Current Outbound Attachment: NOT_APPLICABLE",
            "Manufacturer Communication Intent: NONE",
            "Manufacturer Draft Mode: NONE",
            "Mail Mode: NONE",
            "Protected Value validation: NOT_APPLICABLE",
            "Manufacturer Mail Data Boundary: NOT_APPLICABLE",
            "Manufacturer Draft validation: NOT_APPLICABLE");

        static string ValueOrNotApplicable(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "NOT_APPLICABLE" : value;
    }

    private bool CanUseCreatedArtifactForManufacturerMail() =>
        !turnActive
        && artifactResult is not null
        && !string.IsNullOrWhiteSpace(CreatedArtifactPath)
        && File.Exists(CreatedArtifactPath);

    private async Task UseCreatedArtifactForManufacturerMail()
    {
        var snapshot = caseProvider();
        if (!CanUseCreatedArtifactForManufacturerMail())
        {
            ArtifactWarnings = "メーカー確認メールに使用できる、作成成功済みの英訳ファイルがありません。";
            return;
        }

        try { await SelectManufacturerTranslationAsync(artifactPlan?.SourceFullPath ?? ArtifactSourceFile, CreatedArtifactPath); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or FileNotFoundException)
        {
            ArtifactWarnings = $"作成済み英訳ファイルをメーカー添付に指定できません: {ex.Message}";
            return;
        }

        ArtifactResultText = $"{BuildArtifactResultText(artifactResult!)}{Environment.NewLine}"
            + $"次のメーカー確認メールに使用する英訳ファイルとして指定しました: {Path.GetFileName(pendingManufacturerAttachmentPath)}";
    }

    private async Task ChooseExistingManufacturerTranslationAsync()
    {
        var snapshot = caseProvider();
        var sourceDialog = new WpfOpenFileDialog { Title = "既存英訳ファイルに対応する元ファイルを選択", InitialDirectory = snapshot.CaseFolder,
            Filter = "対応形式|*.xlsx;*.docx;*.pptx;*.csv;*.txt;*.md", CheckFileExists = true };
        if (sourceDialog.ShowDialog() != true) return;
        var outputDialog = new WpfOpenFileDialog { Title = "メーカーへ添付する既存英訳ファイルを選択", InitialDirectory = Path.GetDirectoryName(sourceDialog.FileName),
            Filter = "英訳ファイル|*.*", CheckFileExists = true };
        if (outputDialog.ShowDialog() != true) return;
        await SelectManufacturerTranslationAsync(sourceDialog.FileName, outputDialog.FileName);
    }

    internal async Task SelectManufacturerTranslationAsync(string sourcePath, string outputPath)
    {
        var snapshot = caseProvider();
        var selection = ManufacturerTranslationSelection.Create(snapshot.CaseFolder, sourcePath, outputPath);
        pendingManufacturerAttachmentPath = selection.OutputPath;
        pendingManufacturerAttachmentSourcePath = selection.SourcePath;
        pendingManufacturerAttachmentSupportId = snapshot.SupportId.Trim();
        pendingManufacturerAttachmentCaseFolder = Path.GetFullPath(snapshot.CaseFolder);
        manufacturerMailQualityReady = false;
        if (!Files.Any(file => string.Equals(file.FullPath, pendingManufacturerAttachmentPath, StringComparison.OrdinalIgnoreCase)))
        {
            var info = new FileInfo(pendingManufacturerAttachmentPath);
            var item = new CodexCaseFileViewModel(new CodexCaseFileInfo {
                FullPath = info.FullName, RelativePath = Path.GetRelativePath(snapshot.CaseFolder, info.FullName),
                FileName = info.Name, Size = info.Length, LastModifiedAt = info.LastWriteTimeUtc,
                Kind = CodexCaseFileKind.Document, CanSendToCodex = true }, selected: false);
            item.PropertyChanged += OnCaseFilePropertyChanged;
            Files.Add(item);
        }
        ArtifactWarnings = "警告なし";
        ArtifactResultText = $"メーカー添付として指定しました: {Path.GetFileName(selection.OutputPath)}";
        OnPropertyChanged(nameof(PendingManufacturerAttachmentText));
        OnPropertyChanged(nameof(ManufacturerAttachments));
        OnPropertyChanged(nameof(ManufacturerAttachmentNotice));
        RaiseArtifactCommandStates();
        var saved = currentSession is not null && currentSession.SupportId == snapshot.SupportId
            && string.Equals(currentSession.CaseFolder, snapshot.CaseFolder, StringComparison.OrdinalIgnoreCase)
            ? currentSession : new CodexSession { SupportId = snapshot.SupportId, ProductId = snapshot.ProductId,
            ProductName = snapshot.ProductName, CaseFolder = snapshot.CaseFolder, CreatedAt = DateTimeOffset.Now };
        currentSession = saved with { ManufacturerTranslation = selection, LastUsedAt = DateTimeOffset.Now };
        await sessionStore.SaveAsync(currentSession);
    }

    private void RestoreManufacturerTranslation(CodexSession? session, CodexCaseSnapshot snapshot)
    {
        var selection = session?.ManufacturerTranslation;
        if (selection is null || !string.Equals(Path.GetFullPath(session!.CaseFolder), Path.GetFullPath(snapshot.CaseFolder), StringComparison.OrdinalIgnoreCase)) return;
        pendingManufacturerAttachmentPath = selection.OutputPath;
        pendingManufacturerAttachmentSourcePath = selection.SourcePath;
        pendingManufacturerAttachmentSupportId = snapshot.SupportId.Trim();
        pendingManufacturerAttachmentCaseFolder = Path.GetFullPath(snapshot.CaseFolder);
        if (!selection.IsValid(snapshot.CaseFolder))
            ArtifactWarnings = "指定済み英訳の元ファイルまたは英訳ファイルが変更・削除されています。再指定してください。";
        OnPropertyChanged(nameof(PendingManufacturerAttachmentText));
        OnPropertyChanged(nameof(ManufacturerAttachments));
        OnPropertyChanged(nameof(ManufacturerAttachmentNotice));
    }

    private bool IsPendingManufacturerAttachmentForCase(CodexCaseSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(pendingManufacturerAttachmentPath)
        && string.Equals(pendingManufacturerAttachmentSupportId, snapshot.SupportId.Trim(), StringComparison.Ordinal)
        && string.Equals(
            pendingManufacturerAttachmentCaseFolder,
            Path.GetFullPath(snapshot.CaseFolder),
            StringComparison.OrdinalIgnoreCase);

    private bool TryGetPendingManufacturerAttachment(
        CodexCaseSnapshot snapshot,
        out string attachmentPath,
        out string sourcePath,
        out string error)
    {
        attachmentPath = string.Empty;
        sourcePath = string.Empty;
        error = string.Empty;
        if (!IsPendingManufacturerAttachmentForCase(snapshot))
        {
            return false;
        }

        if (currentSession?.ManufacturerTranslation is { } selection && !selection.IsValid(snapshot.CaseFolder))
        {
            error = "PENDING_MANUFACTURER_ATTACHMENT_CHANGED: 指定済み英訳の元ファイルまたは英訳ファイルが変更・削除されています。元の添付へ戻さず、再指定してください。";
            return false;
        }

        if (!File.Exists(pendingManufacturerAttachmentPath))
        {
            error = "PENDING_MANUFACTURER_ATTACHMENT_MISSING: 指定済みの英訳ファイルが見つかりません。別のファイルへ自動的に切り替えず、翻訳ファイルを再作成または再指定してください。";
            return false;
        }

        try
        {
            attachmentPath = artifactPathPolicy.NormalizeSelectedSourceFile(
                snapshot.CaseFolder,
                pendingManufacturerAttachmentPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or FileNotFoundException)
        {
            error = $"PENDING_MANUFACTURER_ATTACHMENT_INVALID: 指定済みの英訳ファイルは現在案件で使用できません: {ex.Message}";
            attachmentPath = string.Empty;
            return false;
        }

        sourcePath = pendingManufacturerAttachmentSourcePath;
        return true;
    }

    private ManufacturerAttachmentResolution ResolveManufacturerAttachments(CodexCaseSnapshot snapshot)
    {
        TryGetPendingManufacturerAttachment(snapshot, out var output, out var source, out _);
        var inquiry = ResolveCurrentCustomerDeltaSource(snapshot) ?? snapshot.InquiryFile;
        return ManufacturerAttachmentResolver.Resolve(snapshot.CaseFolder, inquiry,
            string.Join(Environment.NewLine, ResolveCurrentCustomerDeltaContent(snapshot, inquiry)),
            source, output, Files.Select(item => item.File));
    }

    private async Task<IReadOnlyList<string>> ReadManufacturerInquiryWorkbookTextAsync(CodexCaseSnapshot snapshot)
    {
        var originals = ResolveManufacturerAttachments(snapshot).Files.Select(file => file.FullPath).ToList();
        if (TryGetPendingManufacturerAttachment(snapshot, out var output, out var source, out _))
        {
            originals.RemoveAll(path => string.Equals(path, output, StringComparison.OrdinalIgnoreCase));
            originals.Add(source);
        }
        var texts = new List<string>();
        foreach (var path in originals.Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(path => Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                         && !IsGeneratedTranslationFile(Path.GetFileName(path))
                         && !string.Equals(artifactPlan?.SourceFullPath, path, StringComparison.OrdinalIgnoreCase)))
        {
            // Reuse the read-only extraction plan; never recreate a translation just to read its inquiry.
            var plan = await excelTranslationService.CreatePlanAsync(new ArtifactCreationRequest {
                CaseFolder = snapshot.CaseFolder, SourceFilePath = path, DestinationFolder = Path.GetDirectoryName(path)!,
                OutputFileName = artifactFilenameTranslationService.CreatePreview(path).OutputFileName });
            texts.AddRange(plan.Excel.Entries.Select(entry => entry.SourceText).Where(text => !string.IsNullOrWhiteSpace(text)));
        }
        return texts;
    }

    private bool HasRecordedCorrespondence(CodexCaseSnapshot snapshot, bool manufacturer)
    {
        return Files.Where(file => file.File.Size > 0 && file.File.Size <= 1024 * 1024
                && Path.GetExtension(file.FullPath).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                && file.FileName.Contains(manufacturer ? "メーカー連携" : "お客様への返信案", StringComparison.Ordinal))
            .Any(file =>
            {
                try
                {
                    artifactPathPolicy.NormalizeSelectedSourceFile(snapshot.CaseFolder, file.FullPath);
                    var content = EncodingPolicy.DecodeNoteText(File.ReadAllBytes(file.FullPath));
                    // An unsent draft, an empty template, or a translated artifact is not a previous exchange.
                    return manufacturer
                        ? LooksLikeManufacturerResponse(content) || content.Contains("メーカーから受信", StringComparison.Ordinal)
                        : content.Contains("顧客へ送信", StringComparison.Ordinal) || content.Contains("お客様へ送信", StringComparison.Ordinal)
                            || content.Contains("送信日時:", StringComparison.Ordinal) || content.Contains("送信日時：", StringComparison.Ordinal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
            });
    }

    private ManufacturerMailCaseContext BuildManufacturerMailCaseContext(
        CodexCaseSnapshot snapshot,
        ManufacturerCommunicationIntent intent,
        IReadOnlyList<string>? workbookText = null)
    {
        var immediateManufacturerResponse = intent == ManufacturerCommunicationIntent.ReplyToManufacturer
            ? FindLatestImmediateManufacturerResponse()
            : string.Empty;
        if (intent == ManufacturerCommunicationIntent.ReplyToManufacturer)
        {
            var sanitizedResponse = ManufacturerMailContentSanitizer.SanitizeLines(
                [immediateManufacturerResponse],
                snapshot.CompanyName,
                snapshot.CustomerName,
                string.Empty);
            return new ManufacturerMailCaseContext
            {
                CommunicationIntent = intent,
                SupportId = snapshot.SupportId,
                ProductName = snapshot.ProductName,
                ProductPromptFilePath = snapshot.ProductPromptFilePath,
                SupportToolSettingsFilePath = snapshot.SupportToolSettingsFilePath,
                ImmediateManufacturerResponse = string.Join(Environment.NewLine, sanitizedResponse),
                ImmediateManufacturerRecipientName = ExtractManufacturerRecipientFirstName(immediateManufacturerResponse),
                CloseRequested = false,
                CustomerReplyAllowed = true,
                ProtectedValues = ManufacturerDraftPairParser.CreateCanonicalProtectedValueSet(
                    snapshot.ProductName,
                    snapshot.SupportId,
                    string.Empty,
                    [],
                    immediateManufacturerResponse),
            };
        }

        var hasPendingAttachment = TryGetPendingManufacturerAttachment(
            snapshot,
            out var pendingAttachmentPath,
            out _,
            out _);
        var currentCustomerDeltaPath = ResolveCurrentCustomerDeltaSource(snapshot);
        var deltaFileName = string.IsNullOrWhiteSpace(currentCustomerDeltaPath)
            ? string.Empty
            : Path.GetFileName(currentCustomerDeltaPath);
        var outputAttachment = hasPendingAttachment
            ? Path.GetFileName(pendingAttachmentPath)
            : string.IsNullOrWhiteSpace(ArtifactOutputFileName)
                ? string.Empty
                : Path.GetFileName(ArtifactOutputFileName);
        var attachments = ResolveManufacturerAttachments(snapshot);
        var outbound = attachments.Files.Where(file => Files.FirstOrDefault(item => item.FullPath == file.FullPath)?.IsManufacturerAttachmentSelected != false)
            .Select(file => file.FileName).ToArray();
        IEnumerable<string> rawDelta = ResolveCurrentCustomerDeltaContent(snapshot, currentCustomerDeltaPath).Concat(workbookText ?? []);
        if (artifactPlan is not null && (string.Equals(artifactPlan.SourceFullPath, currentCustomerDeltaPath, StringComparison.OrdinalIgnoreCase)
            || ManufacturerAttachmentResolver.IsCustomerBundle(artifactPlan.SourceFullPath)))
        {
            rawDelta = rawDelta.Concat(artifactPlan.Format == ArtifactFormat.ExcelWorkbook
                ? artifactPlan.Excel.Entries.Select(entry => entry.SourceText)
                : artifactPlan.Text.Entries.Select(entry => entry.SourceText));
        }
        var deltaContent = ManufacturerMailContentSanitizer.SanitizeLines(rawDelta,
            snapshot.CompanyName, snapshot.CustomerName, outbound);
        var previousManufacturerContact = HasRecordedCorrespondence(snapshot, manufacturer: true);
        var previousCustomerReply = HasRecordedCorrespondence(snapshot, manufacturer: false);
        var closeRequested = deltaContent.Any(ManufacturerMailConcepts.HasCloseRequest);
        var hasCurrentDelta = !string.IsNullOrWhiteSpace(deltaFileName) || deltaContent.Count > 0;
        var followUpAllowed = hasCurrentDelta
            && !string.IsNullOrWhiteSpace(outputAttachment)
            && previousManufacturerContact
            && previousCustomerReply;

        return new ManufacturerMailCaseContext
        {
            CommunicationIntent = intent,
            SupportId = snapshot.SupportId,
            ProductName = snapshot.ProductName,
            ProductPromptFilePath = snapshot.ProductPromptFilePath,
            SupportToolSettingsFilePath = snapshot.SupportToolSettingsFilePath,
            CurrentCustomerDeltaFileName = deltaFileName,
            CurrentCustomerDeltaSourceType = string.IsNullOrWhiteSpace(currentCustomerDeltaPath)
                ? "NONE"
                : "CUSTOMER_INQUIRY",
            CurrentCustomerDeltaContent = deltaContent,
            CurrentOutboundAttachment = outbound.Contains(outputAttachment, StringComparer.OrdinalIgnoreCase) ? outputAttachment : outbound.FirstOrDefault() ?? string.Empty,
            CurrentOutboundAttachments = outbound,
            AttachmentWarnings = attachments.Warnings,
            PreviousManufacturerContact = previousManufacturerContact,
            PreviousCustomerReply = previousCustomerReply,
            CloseRequested = closeRequested,
            ManufacturerFollowupAllowed = followUpAllowed,
            CustomerReplyAllowed = !followUpAllowed,
            ProtectedValues = ManufacturerDraftPairParser.CreateCanonicalProtectedValueSet(
                snapshot.ProductName,
                snapshot.SupportId,
                outbound.Contains(outputAttachment, StringComparer.OrdinalIgnoreCase) ? outputAttachment : string.Empty,
                outbound,
                deltaContent.ToArray()),
        };
    }

    private string FindLatestImmediateManufacturerResponse()
    {
        if (LooksLikeManufacturerResponse(currentManufacturerResponseCandidate))
        {
            return currentManufacturerResponseCandidate;
        }

        var message = Messages
            .LastOrDefault(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                && LooksLikeManufacturerResponse(message.Text));
        if (!string.IsNullOrWhiteSpace(message?.Text))
        {
            return message.Text.Trim();
        }

        return LooksLikeManufacturerResponse(PromptInput) ? PromptInput.Trim() : string.Empty;
    }

    internal string GetManufacturerResponseForContext(CodexCaseSnapshot snapshot)
    {
        var identity = (SupportId: CaseNaming.NormalizeSupportNumber(snapshot.SupportId),
            Product: snapshot.ProductName.Trim().ToUpperInvariant());
        if (promptCaseIdentity != identity)
            return string.Empty;
        if (LooksLikeManufacturerResponse(currentManufacturerResponseCandidate))
            return currentManufacturerResponseCandidate;
        return currentSession is not null
            && string.Equals(CaseNaming.NormalizeSupportNumber(currentSession.SupportId), identity.SupportId, StringComparison.Ordinal)
            && (!snapshot.ProductId.HasValue || !currentSession.ProductId.HasValue
                || snapshot.ProductId == currentSession.ProductId)
            ? FindLatestImmediateManufacturerResponse()
            : string.Empty;
    }

    private static string ExtractManufacturerRecipientFirstName(string text)
    {
        var signature = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .LastOrDefault(static line => line.Contains('|', StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(signature))
        {
            return string.Empty;
        }

        var name = signature.Split('|', 2)[0].Trim();
        var firstName = Regex.Match(name, @"^[A-Za-z]+(?:['-][A-Za-z]+)?", RegexOptions.CultureInvariant).Value;
        return firstName;
    }

    private void ClearStaleManufacturerTechnicalAnswer()
    {
        if (LooksLikeManufacturerMail(TechnicalAnswer))
        {
            TechnicalAnswer = string.Empty;
        }
    }

    private static bool LooksLikeManufacturerMail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hasSubject = text.Contains("件名:", StringComparison.Ordinal)
            || text.Contains("件名：", StringComparison.Ordinal)
            || text.Contains("Subject:", StringComparison.OrdinalIgnoreCase);
        var hasManufacturerGreeting = text.Contains("メーカーサポート", StringComparison.Ordinal)
            || text.Contains("Hello Support Team", StringComparison.OrdinalIgnoreCase);
        var hasClosing = text.Contains("よろしくお願いいたします", StringComparison.Ordinal)
            || text.Contains("Best regards", StringComparison.OrdinalIgnoreCase);
        return hasSubject && hasManufacturerGreeting && hasClosing;
    }

    private static bool LooksLikeManufacturerResponse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hasSignature = text.Contains('|', StringComparison.Ordinal)
            && (text.Contains("Regards", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Best regards", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Technical Support Engineer", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Technical Support", StringComparison.OrdinalIgnoreCase));
        var hasResponseLanguage = text.Contains("I have heard back", StringComparison.OrdinalIgnoreCase)
            || text.Contains("We support", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Thank you for your", StringComparison.OrdinalIgnoreCase)
            || text.Contains("engineering", StringComparison.OrdinalIgnoreCase)
            || text.Contains("回答", StringComparison.Ordinal);
        return hasSignature || (hasResponseLanguage && text.Contains("Regards", StringComparison.OrdinalIgnoreCase));
    }

    private void ClearUnrequestedArtifactTranslationPlan()
    {
        artifactPlan = null;
        artifactPlanReadyForExecution = false;
        artifactResult = null;
        artifactTranslations = [];
        artifactTextTranslations = [];
        ArtifactSourceFile = string.Empty;
        artifactSourceExplicitlySelected = false;
        artifactOutputFileName = "Inquiry_Details_EN.xlsx";
        OnPropertyChanged(nameof(ArtifactOutputFileName));
        ArtifactTranslationPreview.Clear();
        ArtifactPreviewItems.Clear();
        ArtifactStateText = "未計画";
        ArtifactProgressPercent = 0;
        ArtifactWarnings = "警告なし";
        ArtifactResultText = "翻訳成果物は要求されていません。";
        NotifyArtifactPlanProperties();
    }

    private static string FormatBoundaryViolations(IReadOnlyList<string> violations)
    {
        if (violations.Count == 0)
        {
            return "なし";
        }

        return string.Join(
            Environment.NewLine,
            violations.Select(static violation => violation switch
            {
                _ when violation.StartsWith("PriorAttachment:", StringComparison.Ordinal) =>
                    $"Category: Attachment / Detected value: {violation["PriorAttachment:".Length..]} / Reason: 過去添付は今回のCurrentOutboundAttachmentsに含まれていません。",
                _ when violation.StartsWith("Attachment:", StringComparison.Ordinal) =>
                    $"Category: Attachment / Detected value: {violation["Attachment:".Length..]} / Reason: CurrentOutboundAttachmentsで許可されたファイル名ではありません。",
                _ when violation.StartsWith("Customer", StringComparison.Ordinal) =>
                    $"Category: CustomerPII / Detected value: (redacted) / Reason: 顧客識別情報はメーカー向け案に含められません。",
                _ when violation.StartsWith("Internal:", StringComparison.Ordinal) =>
                    $"Category: InternalState / Detected value: {violation["Internal:".Length..]} / Reason: 内部処理用語はメーカー向け案に含められません。",
                _ => $"Category: DataBoundary / Detected value: (redacted) / Reason: 許可リスト外の情報です。",
            }));
    }

    private string FindGeneratedArtifactFileName()
    {
        return Files
            .Where(static file => IsGeneratedTranslationFile(file.FileName))
            .Where(static file => File.Exists(file.FullPath))
            .OrderByDescending(static file => file.File.LastModifiedAt)
            .Select(static file => file.FileName)
            .FirstOrDefault() ?? string.Empty;
    }

    private void CopyManufacturerDraft(string draft)
    {
        if (!string.IsNullOrWhiteSpace(draft))
        {
            clipboardWriter(draft);
        }
    }

    private bool CanSendEnglishManufacturerDraftToWpfNote() => !turnActive
        && manufacturerMailQualityReady
        && !string.IsNullOrWhiteSpace(EnglishManufacturerDraft)
        && !string.IsNullOrWhiteSpace(currentSnapshot?.NoteEditorTransferPipeName)
        && sendToWpfNoteEditor is not null;

    private async Task SendEnglishManufacturerDraftToWpfNoteAsync()
    {
        var value = EnglishManufacturerDraft;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (currentSnapshot?.UseRagLabEvidence == true && ContainsRagLabInternalMarker(value))
        {
            WarningText = "WPFノート編集へのコピーを中止しました。内部処理用語が含まれているため、内容を確認してください。";
            ConnectionDetails = "英語メーカー確認案をWPFノート編集へコピーしていません。";
            return;
        }

        var pipeName = currentSnapshot?.NoteEditorTransferPipeName;
        if (string.IsNullOrWhiteSpace(pipeName) || sendToWpfNoteEditor is null)
        {
            WarningText = "WPFノート編集へ送信できません。親WPFからAI回答支援を起動してください。";
            ConnectionDetails = "英語メーカー確認案をWPFノート編集へコピーしていません。";
            return;
        }

        var sent = await sendToWpfNoteEditor(value).ConfigureAwait(false);
        RunOnUi(() =>
        {
            if (sent)
            {
                WarningText = string.Empty;
                ConnectionDetails = "英語メーカー確認案をWPFノート編集へコピーしました。案件ファイルは変更していません。";
            }
            else
            {
                WarningText = "WPFノート編集へ送信できませんでした。親WPFが起動中か確認してください。";
                ConnectionDetails = "英語メーカー確認案をWPFノート編集へコピーしていません。";
            }
        });
    }

    private void ClearManufacturerDrafts()
    {
        manufacturerMailQualityReady = false;
        JapaneseManufacturerDraft = string.Empty;
        EnglishManufacturerDraft = string.Empty;
    }

    private bool IsManufacturerDraftPayload(string text)
    {
        return manufacturerDraftPairParser.Parse(text).Succeeded;
    }

    private async Task<string> SendArtifactTurnAsync(
        string prompt,
        string displayInstruction,
        bool showResponseInChat = true)
    {
        if (string.IsNullOrWhiteSpace(client.CurrentThreadId))
        {
            await StartNewAsync().ConfigureAwait(false);
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        artifactTurnCompletion = completion;
        artifactTurnActive = true;
        artifactTurnResponseVisibleInChat = showResponseInChat;
        artifactTurnResponseBuffer.Clear();
        manufacturerDraftTurnActive = displayInstruction.Contains("メーカー", StringComparison.Ordinal);
        RunOnUi(() =>
        {
            Messages.Add(new CodexChatMessageViewModel
            {
                Role = "user",
                Text = displayInstruction,
                CreatedAt = DateTimeOffset.Now,
            });
            if (showResponseInChat)
            {
                var assistantMessage = new CodexChatMessageViewModel
                {
                    Role = "assistant",
                    CreatedAt = DateTimeOffset.Now,
                    IsStreaming = true,
                };
                Messages.Add(assistantMessage);
                currentAssistantMessage = assistantMessage;
            }
            else
            {
                currentAssistantMessage = null;
            }
            turnActive = true;
            ConnectionDetails = "Codexへ成果物用の構造化データを依頼しています。ファイル書込みは行わせません。";
            RaiseCommandStates();
        });

        try
        {
            var turn = manufacturerDraftTurnActive
                ? await client.StartIsolatedDraftTurnAsync(prompt, currentSession?.Model, currentSession?.ReasoningEffort).ConfigureAwait(false)
                : await client.StartTurnAsync(prompt).ConfigureAwait(false);
            if (!manufacturerDraftTurnActive) hasSentInitialContext = true;
            if (currentSession is not null && turnActive && !manufacturerDraftTurnActive)
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
            artifactTurnActive = false;
            artifactTurnResponseVisibleInChat = true;
            artifactTurnResponseBuffer.Clear();
            manufacturerDraftTurnActive = false;
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
            .Where(static file => CaseArtifactPathPolicy.IsSupportedSource(file.FullPath))
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new FileNotFoundException("案件フォルダ内に翻訳保存に対応するファイルが見つかりません。案件ファイルを再読込してください。");
        }

        var mentioned = artifactRequestDetector.FindMentionedSourceFileName(instruction);
        var selected = !string.IsNullOrWhiteSpace(mentioned)
            ? candidates.FirstOrDefault(item => string.Equals(item.FileName, mentioned, StringComparison.OrdinalIgnoreCase))
            : null;
        if (selected is null)
        {
            selected = FindCurrentCustomerDeltaSource(caseProvider()) is { } currentDelta
                ? candidates.FirstOrDefault(item => string.Equals(item.FullPath, currentDelta, StringComparison.OrdinalIgnoreCase))
                : null;
        }

        var originalFiles = candidates
            .Where(static file => !IsGeneratedTranslationFile(file.FileName))
            .Where(file => !HasExistingTranslationArtifact(file, candidates))
            .ToArray();
        selected ??= originalFiles.FirstOrDefault(static file => file.FileName.Contains("問い合わせ内容", StringComparison.OrdinalIgnoreCase));
        selected ??= originalFiles.FirstOrDefault();
        if (selected is null)
        {
            throw new FileNotFoundException("自動選択できる翻訳元ファイルが見つかりません。翻訳済みファイルを使う場合は「翻訳元ファイルを選択」で明示してください。");
        }

        return selected.FullPath;
    }

    private bool HasExistingTranslationArtifact(
        CodexCaseFileViewModel source,
        IReadOnlyList<CodexCaseFileViewModel> candidates)
    {
        var outputName = artifactFilenameTranslationService.CreatePreview(source.FullPath).OutputFileName;
        return candidates.Any(file => !string.Equals(file.FullPath, source.FullPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(file.FileName, outputName, StringComparison.OrdinalIgnoreCase));
    }

    private void ChooseArtifactSource()
    {
        var caseFolder = caseProvider().CaseFolder;
        if (!Directory.Exists(caseFolder))
        {
            ArtifactWarnings = "案件フォルダが見つからないため、翻訳元ファイルを選択できません。";
            return;
        }

        var dialog = new WpfOpenFileDialog
        {
            Title = "翻訳元ファイルを選択",
            Filter = "案件内ファイル|*.*|対応形式|*.xlsx;*.docx;*.pptx;*.csv;*.txt;*.md",
            InitialDirectory = caseFolder,
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var selected = artifactPathPolicy.NormalizeSelectedSourceFile(caseFolder, dialog.FileName);
            SetArtifactSource(selected);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or FileNotFoundException)
        {
            ArtifactWarnings = ex.Message;
            ArtifactStateText = "未計画";
        }
    }

    private void SetArtifactSource(string sourceFilePath, bool userSelected = true)
    {
        artifactPlan = null;
        artifactPlanReadyForExecution = false;
        artifactResult = null;
        artifactTranslations = [];
        artifactTextTranslations = [];
        ArtifactSourceFile = sourceFilePath;
        artifactSourceExplicitlySelected = userSelected;
        var filenamePreview = artifactFilenameTranslationService.CreatePreview(sourceFilePath);
        artifactOutputFileName = filenamePreview.OutputFileName;
        OnPropertyChanged(nameof(ArtifactOutputFileName));
        CreatedArtifactPath = string.Empty;
        ClearManufacturerDrafts();
        ArtifactTranslationPreview.Clear();
        ArtifactPreviewItems.Clear();
        ArtifactWarnings = !CaseArtifactPathPolicy.IsSupportedSource(sourceFilePath)
            ? "このファイル形式は現在、翻訳保存に対応していません。"
            : filenamePreview.Warning;
        ArtifactResultText = "翻訳元ファイルを選択しました。「実行内容を確認」で形式と対象を確認してください。";
        ArtifactStateText = "元ファイル選択済み";
        ArtifactProgressPercent = 0;
        NotifyArtifactPlanProperties();
        RaiseArtifactCommandStates();
    }

    private static bool IsGeneratedTranslationFile(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var marker = stem.LastIndexOf("_EN", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        var suffix = stem[(marker + 3)..];
        return suffix.Length == 0
            || suffix.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .All(static part => part.Length > 0 && part.All(char.IsAsciiDigit));
    }

    private string? FindCurrentCustomerDeltaSource(CodexCaseSnapshot snapshot)
    {
        return Files
            .Where(static file => CaseArtifactPathPolicy.IsSupportedSource(file.FullPath))
            .Where(file => file.File.Kind == CodexCaseFileKind.CustomerInquiry)
            .Where(file => !IsGeneratedTranslationFile(file.FileName))
            .Where(file => file.FileName.Contains("追加問い合わせ", StringComparison.OrdinalIgnoreCase)
                || file.RelativePath.Contains("追加問い合わせ", StringComparison.OrdinalIgnoreCase)
                || file.RelativePath.Contains("additional_inquiry", StringComparison.OrdinalIgnoreCase)
                || file.RelativePath.Contains("additional-inquiry", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static file => file.File.LastModifiedAt)
            .Select(static file => file.FullPath)
            .FirstOrDefault();
    }

    private string? ResolveCurrentCustomerDeltaSource(CodexCaseSnapshot snapshot)
    {
        var additionalInquiry = FindCurrentCustomerDeltaSource(snapshot);
        if (!string.IsNullOrWhiteSpace(additionalInquiry))
        {
            return additionalInquiry;
        }

        if (string.IsNullOrWhiteSpace(snapshot.InquiryFile))
        {
            return null;
        }

        var inquiryFileName = Path.GetFileName(snapshot.InquiryFile);
        return Files
            .Where(file => file.File.Kind == CodexCaseFileKind.CustomerInquiry)
            .Where(file => !IsGeneratedTranslationFile(file.FileName))
            .Where(file => string.Equals(file.FullPath, snapshot.InquiryFile, StringComparison.OrdinalIgnoreCase)
                || string.Equals(file.FileName, inquiryFileName, StringComparison.OrdinalIgnoreCase))
            .Select(static file => file.FullPath)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> ResolveCurrentCustomerDeltaContent(
        CodexCaseSnapshot snapshot,
        string? currentCustomerDeltaPath)
    {
        if (string.IsNullOrWhiteSpace(currentCustomerDeltaPath))
        {
            return [];
        }

        // InquiryText is a retrieval focus/edited excerpt, not the complete inbound message.
        // Read the latest recorded inquiry so attachments and concrete requests are not lost.
        if (Path.GetExtension(currentCustomerDeltaPath).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var path = new CaseArtifactPathPolicy().NormalizeSelectedSourceFile(snapshot.CaseFolder, currentCustomerDeltaPath);
                if (new FileInfo(path).Length <= 1024 * 1024)
                {
                    var entries = CaseNoteHistoryParser.Parse(EncodingPolicy.DecodeNoteText(File.ReadAllBytes(path)));
                    var inbound = entries.Where(entry => entry.Header.Contains("顧客から受信", StringComparison.Ordinal)
                        || entry.Header.Contains("お客様から受信", StringComparison.Ordinal)).ToArray();
                    var latest = CaseNoteHistoryParser.PickLatest(inbound.Length > 0 ? inbound : entries);
                    if (!string.IsNullOrWhiteSpace(latest?.Body)) return [latest.Body];
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        }

        if ((string.Equals(currentCustomerDeltaPath, snapshot.InquiryFile, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.GetFileName(currentCustomerDeltaPath),
                    Path.GetFileName(snapshot.InquiryFile),
                    StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(snapshot.InquiryText))
        {
            return [snapshot.InquiryText];
        }

        var fileName = Path.GetFileName(currentCustomerDeltaPath);
        return snapshot.Evidence
            .Where(source => string.Equals(Path.GetFileName(source.Title), fileName, StringComparison.OrdinalIgnoreCase))
            .Select(static source => source.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
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

    private ArtifactPromptContext BuildArtifactPromptContext(
        IReadOnlyList<string>? attachmentNames = null,
        string translationSummary = "",
        string outputFileName = "")
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

    private SearchSource? BuildCurrentArtifactEvidence(CodexCaseSnapshot snapshot)
    {
        if (artifactPlan is null
            || !string.Equals(
                Path.GetFileName(artifactPlan.SourceFullPath),
                Path.GetFileName(ArtifactSourceFile),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = artifactPlan.Format == ArtifactFormat.ExcelWorkbook
            ? string.Join(
                Environment.NewLine,
                artifactPlan.Excel.Entries
                    .Select(static entry => entry.SourceText)
                    .Where(static value => !string.IsNullOrWhiteSpace(value)))
            : string.Join(
                Environment.NewLine,
                artifactPlan.Text.Entries
                    .Select(static entry => entry.SourceText)
                    .Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new SearchSource
        {
            SourceId = $"artifact-plan:{artifactPlan.PlanId}",
            SourceType = "CurrentCase",
            Title = Path.GetFileName(artifactPlan.SourceFullPath),
            Text = text,
            SupportNumber = snapshot.SupportId,
            SourceRole = "ArtifactTranslationSource",
            EvidenceKind = "ArtifactPlanSource",
        };
    }

    private static string BuildCurrentCaseEvidenceReferences(IReadOnlyList<SearchSource> sources)
    {
        var currentCaseSources = sources
            .Where(static source => string.Equals(source.SourceType, "CurrentCase", StringComparison.OrdinalIgnoreCase))
            .Take(24)
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
            builder.AppendLine($"  Role: {source.SourceRole ?? "CaseBackground"}");
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

        foreach (var item in ArtifactPreviewItems)
        {
            if (lookup.TryGetValue(item.Key, out var translation))
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

    private void UseDatedArtifactName()
    {
        if (string.IsNullOrWhiteSpace(ArtifactDestinationFolder)
            || string.IsNullOrWhiteSpace(ArtifactSourceFile))
        {
            return;
        }

        ArtifactOutputFileName = artifactPathPolicy.SuggestDateFileNameFromOutput(
            ArtifactDestinationFolder,
            ArtifactOutputFileName,
            DateTime.Today);
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
        artifactTextTranslations = [];
        artifactRequestInstruction = string.Empty;
        manufacturerRecipient = new();
        ArtifactStateText = "未計画";
        ArtifactProgressPercent = 0;
        ArtifactSourceFile = string.Empty;
        artifactSourceExplicitlySelected = false;
        artifactDestinationFolder = string.Empty;
        OnPropertyChanged(nameof(ArtifactDestinationFolder));
        artifactOutputFileName = "Inquiry_Details_EN.xlsx";
        OnPropertyChanged(nameof(ArtifactOutputFileName));
        ArtifactWarnings = string.Empty;
        ArtifactResultText = string.Empty;
        CreatedArtifactPath = string.Empty;
        pendingManufacturerAttachmentPath = string.Empty;
        pendingManufacturerAttachmentSourcePath = string.Empty;
        pendingManufacturerAttachmentSupportId = string.Empty;
        pendingManufacturerAttachmentCaseFolder = string.Empty;
        OnPropertyChanged(nameof(PendingManufacturerAttachmentText));
        OnPropertyChanged(nameof(ManufacturerRecipientText));
        ClearManufacturerDrafts();
        ArtifactTranslationPreview.Clear();
        ArtifactPreviewItems.Clear();
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
        OnPropertyChanged(nameof(ArtifactOutputPlanText));
        OnPropertyChanged(nameof(ArtifactFormatText));
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
            ReviseManufacturerMailCommand?.RaiseCanExecuteChanged();
            CancelArtifactCommand?.RaiseCanExecuteChanged();
            ChooseArtifactDestinationCommand?.RaiseCanExecuteChanged();
            ChooseArtifactSourceCommand?.RaiseCanExecuteChanged();
            ResetArtifactOutputNameCommand?.RaiseCanExecuteChanged();
            UseNumberedArtifactNameCommand?.RaiseCanExecuteChanged();
            UseDatedArtifactNameCommand?.RaiseCanExecuteChanged();
            OpenArtifactSourceCommand?.RaiseCanExecuteChanged();
            OpenArtifactDestinationCommand?.RaiseCanExecuteChanged();
            OpenCreatedArtifactCommand?.RaiseCanExecuteChanged();
            UseCreatedArtifactForManufacturerMailCommand?.RaiseCanExecuteChanged();
            ChooseExistingManufacturerTranslationCommand?.RaiseCanExecuteChanged();
            CopyJapaneseManufacturerDraftCommand?.RaiseCanExecuteChanged();
            CopyEnglishManufacturerDraftCommand?.RaiseCanExecuteChanged();
            SendEnglishManufacturerDraftToWpfNoteCommand?.RaiseCanExecuteChanged();
            CopyManufacturerMailCommand?.RaiseCanExecuteChanged();
        });
    }

    private void ApplyTextTranslationPreview(IReadOnlyList<ArtifactTextTranslationValue> translations)
    {
        var lookup = translations.ToDictionary(static item => item.Key, StringComparer.Ordinal);
        foreach (var item in ArtifactPreviewItems)
        {
            if (lookup.TryGetValue(item.Key, out var translation))
            {
                item.TranslatedText = translation.TranslatedText;
            }
        }
    }

    private static string FormatName(ArtifactFormat format) => format switch
    {
        ArtifactFormat.ExcelWorkbook => "Excel Workbook (.xlsx)",
        ArtifactFormat.WordDocument => "Word Document (.docx)",
        ArtifactFormat.PowerPointPresentation => "PowerPoint Presentation (.pptx)",
        ArtifactFormat.Csv => "CSV (.csv)",
        ArtifactFormat.PlainText => "Text (.txt)",
        ArtifactFormat.Markdown => "Markdown (.md)",
        _ => "未対応形式",
    };
}
