using System.Collections.ObjectModel;
using SupportCaseManager.AiAssistant.App.Diagnostics;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly QuickDiagnosticService quickDiagnosticService = new();
    private CancellationTokenSource? quickDiagnosticCancellation;
    private string quickDiagnosticState = "未実行";
    private string quickDiagnosticOverall = "-";
    private string quickDiagnosticElapsed = "-";
    private QuickDiagnosticItem? selectedQuickDiagnosticItem;

    public ObservableCollection<QuickDiagnosticItem> QuickDiagnosticItems { get; } = [];
    public AsyncRelayCommand RunQuickDiagnosticCommand { get; }
    public RelayCommand CancelQuickDiagnosticCommand { get; }

    public string QuickDiagnosticState
    {
        get => quickDiagnosticState;
        private set => SetProperty(ref quickDiagnosticState, value);
    }

    public string QuickDiagnosticOverall
    {
        get => quickDiagnosticOverall;
        private set => SetProperty(ref quickDiagnosticOverall, value);
    }

    public string QuickDiagnosticElapsed
    {
        get => quickDiagnosticElapsed;
        private set => SetProperty(ref quickDiagnosticElapsed, value);
    }

    public QuickDiagnosticItem? SelectedQuickDiagnosticItem
    {
        get => selectedQuickDiagnosticItem;
        set => SetProperty(ref selectedQuickDiagnosticItem, value);
    }

    private async Task RunQuickDiagnosticAsync()
    {
        var caseSnapshot = BuildCodexCaseSnapshot();
        var snapshot = new QuickDiagnosticSnapshot(
            caseSnapshot.SupportId, caseSnapshot.ProductName, caseSnapshot.CaseFolder,
            BaseFolder, CloseFolder, caseSnapshot.ProductPromptFilePath,
            caseSnapshot.SupportToolSettingsFilePath, currentCaseContext,
            gptHandoffContext, Notes.ToArray(), caseSnapshot.Evidence.ToArray(),
            AiIndexFolder,
            !string.IsNullOrWhiteSpace(CustomerReplyDraft) || !string.IsNullOrWhiteSpace(Codex?.EnglishManufacturerDraft),
            QualityReviewEnabled, QualityLastRetrievalCount, qualityMemoryStore.FilePath);

        using var cancellation = new CancellationTokenSource();
        quickDiagnosticCancellation = cancellation;
        QuickDiagnosticState = "診断中";
        QuickDiagnosticOverall = "-";
        QuickDiagnosticElapsed = "-";
        QuickDiagnosticItems.Clear();
        SelectedQuickDiagnosticItem = null;
        try
        {
            var report = await quickDiagnosticService.RunAsync(snapshot, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            foreach (var item in report.Items) QuickDiagnosticItems.Add(item);
            QuickDiagnosticOverall = report.Overall;
            QuickDiagnosticElapsed = $"{report.Elapsed.TotalMilliseconds:0} ms";
            QuickDiagnosticState = "完了";
        }
        catch (OperationCanceledException)
        {
            QuickDiagnosticState = "中止";
        }
        catch (Exception ex)
        {
            QuickDiagnosticOverall = "FAILED";
            QuickDiagnosticState = "診断失敗";
            QuickDiagnosticItems.Add(new QuickDiagnosticItem("Quick Diagnostic", QuickDiagnosticStatus.Fail,
                $"{ex.GetType().Name}: 診断を完了できませんでした"));
        }
        finally
        {
            quickDiagnosticCancellation = null;
        }
    }

    private void CancelQuickDiagnostic() => quickDiagnosticCancellation?.Cancel();
}
