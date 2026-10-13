using System.IO;
using System.Windows;
using SupportCaseManager.App.Dialogs;
using SupportCaseManager.App.Outlook;
using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Notes;
using SupportCaseManager.Core.Repository;
using MessageBox = System.Windows.MessageBox;

namespace SupportCaseManager.App;

public partial class MainWindow
{
    private OutlookAcceptanceMonitor? _acceptanceMonitor;
    private CancellationTokenSource? _acceptanceCts;
    private bool _acceptanceScanActive;
    private int _acceptancePendingCount;

    private void StartAcceptanceMonitoring()
    {
        try
        {
            _acceptanceMonitor = new OutlookAcceptanceMonitor(_config, _settings, logger: _logger);
            _acceptanceMonitor.Enable(DateTime.UtcNow);
            _acceptanceCts = new CancellationTokenSource();
            _ = RefreshAcceptanceAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to enable Outlook acceptance detection", ex);
            _viewModel.StatusMessage = "受付メール検知を開始できませんでした。";
        }
    }

    private async Task RefreshAcceptanceAsync()
    {
        if (_acceptanceMonitor is null || _acceptanceScanActive || _acceptanceCts is null) return;
        _acceptanceScanActive = true;
        var token = _acceptanceCts.Token;
        try
        {
            var currentTab = MainTabControl.SelectedItem is System.Windows.Controls.TabItem tab
                ? tab.Header?.ToString() ?? "UNKNOWN" : "UNKNOWN";
            _logger.Info($"ACCEPT_CURRENT_TAB={currentTab}");
            var scannedAt = DateTime.UtcNow;
            var candidates = await _acceptanceMonitor.ScanAsync(scannedAt, token);
            _acceptancePendingCount = candidates.Count;
            var unresolved = false;
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (await Task.Run(() => IsSupportRegistered(candidate.SupportId), token))
                {
                    _logger.Info($"ACCEPT_ALREADY_REGISTERED={candidate.SupportId}");
                    _acceptancePendingCount--;
                    continue;
                }
                if (!await _acceptanceMonitor.RevalidateAsync(candidate, token))
                {
                    _logger.Info($"ACCEPT_FILTERED={candidate.SupportId}:REVALIDATION_FAILED");
                    unresolved = true;
                    continue;
                }
                _logger.Info($"ACCEPT_DIALOG_DISPATCH={candidate.SupportId}:UI={Dispatcher.CheckAccess()}");
                var dialog = new OutlookAcceptanceDialog(candidate, _settings.Products) { Owner = this };
                dialog.Loaded += (_, _) => _logger.Info($"ACCEPT_DIALOG_SHOWN={candidate.SupportId}");
                var choice = dialog.ShowDialog();
                if (choice == false && dialog.Rejected)
                {
                    _acceptanceMonitor.MarkProcessed(candidate, rejected: true);
                    _acceptancePendingCount--;
                    continue;
                }
                if (choice != true) { unresolved = true; continue; }
                if (await Task.Run(() => IsSupportRegistered(candidate.SupportId), token))
                {
                    _viewModel.StatusMessage = $"サポートID {candidate.SupportId} は登録済みです。";
                    continue;
                }
                if (!await _acceptanceMonitor.RevalidateAsync(candidate, token))
                {
                    MessageBox.Show(this, "受付メールを再確認できませんでした。登録していません。",
                        "受付メール", MessageBoxButton.OK, MessageBoxImage.Warning);
                    unresolved = true;
                    continue;
                }
                if (RegisterAcceptance(candidate, dialog))
                {
                    _acceptanceMonitor.MarkProcessed(candidate, rejected: false);
                    _acceptancePendingCount--;
                }
                else unresolved = true;
            }
            if (!unresolved) _acceptanceMonitor.CompleteScan(scannedAt);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error("Outlook acceptance detection failed", ex);
            _viewModel.StatusMessage = "受付メールの検知に失敗しました。次回の監視で再試行します。";
        }
        finally { _acceptanceScanActive = false; }
    }

    private bool IsSupportRegistered(string supportId)
    {
        foreach (var root in _settings.Products.SelectMany(static product => new[] { product.BasePath, product.ClosedPath })
                     .Where(static path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            var repository = new CaseRepository(_logger);
            repository.SetBasePath(root);
            if (repository.FindBySupport(supportId) is not null) return true;
        }
        return false;
    }

    private bool RegisterAcceptance(OutlookAcceptanceCandidate candidate, OutlookAcceptanceDialog dialog)
    {
        var tab = MainTabControl.Items.OfType<System.Windows.Controls.TabItem>()
            .FirstOrDefault(item => item.Tag is SupportCaseManager.Core.Config.ProductProfile profile
                && profile.IsEnabled && profile.Name == dialog.ProductName);
        if (tab is null)
        {
            MessageBox.Show(this, "選択した製品の登録先を確認できません。", "受付メール", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        MainTabControl.SelectedItem = tab;
        OnNewCase(this, new RoutedEventArgs());
        CompanyTextBox.Text = dialog.Company;
        SupportTextBox.Text = candidate.SupportId;
        CreatedDatePicker.SelectedDate = candidate.ReceivedAt.Date;
        var openAfter = OpenAfterCheckBox.IsChecked;
        OpenAfterCheckBox.IsChecked = false;
        CaseRecord? record;
        try { record = TryCreateCase(); }
        finally { OpenAfterCheckBox.IsChecked = openAfter; }
        if (record is null) return false;
        var note = OutlookAcceptanceCandidate.BuildInquiryNote(dialog.Subject, dialog.CustomerName,
            dialog.CustomerEmail, dialog.OriginalSentAt, dialog.InquiryBody);
        try
        {
            NoteService.AppendNote(record.FolderPath, NoteDefinitions.GetByKey("consult"),
                record.SupportNumber, "顧客から受信", note);
            _viewModel.StatusMessage = $"受付メールから案件 {candidate.SupportId} を登録しました。";
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to append original customer inquiry after case registration", ex);
            MessageBox.Show(this, "案件は作成されましたが、お客様相談内容の保存に失敗しました。手動で内容を確認してください。",
                "受付メール", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async void OnAcceptanceDryRun(object sender, RoutedEventArgs e)
    {
        if (_acceptanceMonitor is null) return;
        var input = new InputDialog("受付メール DRY RUN", "過去30日以内のサポートIDを入力してください:") { Owner = this };
        if (input.ShowDialog() != true) return;
        var id = CaseNaming.NormalizeSupportNumber(input.Value);
        if (id.Length != 8 || !id.All(char.IsAsciiDigit))
        {
            MessageBox.Show(this, "8桁のサポートIDを入力してください。", "DRY RUN", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var found = await _acceptanceMonitor.DryRunAsync(DateTime.UtcNow.AddDays(-30), CancellationToken.None);
            var candidate = found.FirstOrDefault(item => item.SupportId == id);
            var state = _acceptanceMonitor.Diagnostics;
            var suggestedProducts = candidate is null ? Array.Empty<string>() : _settings.Products
                .Where(static product => product.IsEnabled)
                .Where(product => candidate.OriginalSubject.Contains(product.Name, StringComparison.OrdinalIgnoreCase)
                    || product.Aliases.Any(alias => alias.Length > 2
                        && candidate.OriginalSubject.Contains(alias, StringComparison.OrdinalIgnoreCase)))
                .Select(static product => product.Name).Take(2).ToArray();
            var text = candidate is null
                ? $"Acceptance mail: NO\nSupport ID: {id}\nCurrent user evidence: NOT FOUND / template mismatch"
                : $"Acceptance mail: YES\nSupport ID: {id}\nCurrent user evidence: FOUND ({candidate.EvidenceLocation})"
                    + $"\nCustomer: {candidate.CustomerName}\nCustomer email: {candidate.CustomerEmail}"
                    + $"\nSubject: {candidate.OriginalSubject}\nOriginal Message: {(candidate.HasOriginalMessage ? "EXTRACTED" : "FAILED")}"
                    + $"\nProduct suggestion: {(suggestedProducts.Length == 1 ? suggestedProducts[0] : "NONE / AMBIGUOUS")}"
                    + "\nDetection Reason: ACCEPTANCE_TEMPLATE, SUPPORT_ID_FOUND, CURRENT_USER_EMAIL_FOUND";
            MessageBox.Show(this, text + $"\nDetection enabled at: {state.EnabledAt:O}"
                + $"\nLast successful scan: {state.LastScan:O}\nPending: {_acceptancePendingCount}"
                + $"\nProcessed: {state.Processed}\nRejected: {state.Rejected}"
                + "\nDRY RUN: 登録・保存・Outlook変更なし", "受付メール DRY RUN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.Error("Acceptance dry run failed", ex);
            MessageBox.Show(this, "Outlookの受付メールを読み取れませんでした。", "DRY RUN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
