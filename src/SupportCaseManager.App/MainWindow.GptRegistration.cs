using System.IO;
using System.Windows;
using System.Windows.Input;
using SupportCaseManager.App.ChatGpt;
using SupportCaseManager.App.Dialogs;
using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Compatibility;
using SupportCaseManager.Core.Notes;
using MessageBox = System.Windows.MessageBox;

namespace SupportCaseManager.App;

public partial class MainWindow
{
    private async Task<bool> TryHandleRegisteredGptDoubleClickAsync()
    {
        var registration = _currentCase?.GptRegistration;
        if (_currentCase is null || registration is null ||
            string.Equals(registration.RegistrationState, GptRegistrationStates.Unregistered, StringComparison.Ordinal))
        {
            return false;
        }

        if (registration.IsRegistered &&
            GptConversationUrl.TryValidateConversation(registration.ConversationUrl, out _))
        {
            try
            {
                await _gptCaseRegistrationService.OpenAsync(registration.ConversationUrl);
                _viewModel.StatusMessage = "登録済みGPT案件チャットを開きました。";
            }
            catch
            {
                MessageBox.Show(
                    this,
                    "登録済みGPTチャットを開けませんでした。GPT操作から再紐付けしてください。",
                    "GPT登録情報",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return true;
        }

        MessageBox.Show(
            this,
            "GPT登録情報の再紐付けが必要です。GPT操作からConversation URLを指定してください。",
            "GPT登録情報",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return true;
    }

    private async void OnGptRegistrationAction(object sender, RoutedEventArgs e)
    {
        if (_isGptRegistrationInProgress)
        {
            return;
        }

        _isGptRegistrationInProgress = true;
        UpdateGptRegistrationUi();
        try
        {
            await HandleGptRegistrationActionAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("GPT案件登録処理に失敗しました。", ex);
            _viewModel.StatusMessage = "GPT案件登録処理に失敗しました。";
            MessageBox.Show(
                this,
                "GPT案件登録処理を完了できませんでした。案件情報は変更していません。",
                "GPT登録",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isGptRegistrationInProgress = false;
            UpdateGptRegistrationUi();
        }
    }

    private async Task HandleGptRegistrationActionAsync()
    {
        if (!TryGetGptRegistrationContext(out var caseRecord, out var productName, out var target))
        {
            return;
        }

        var registration = caseRecord.GptRegistration;
        if (HasGptRegistrationMismatch(caseRecord, productName, target))
        {
            MessageBox.Show(
                this,
                "保存済みGPT登録情報と現在案件の製品が一致しません。登録情報は変更していません。",
                "GPT登録情報",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (registration.IsRegistered)
        {
            var registeredAt = DateTimeOffset.TryParse(registration.RegisteredAt, out var parsed)
                ? parsed.ToLocalTime().ToString("yyyy/MM/dd HH:mm")
                : "不明";
            var open = MessageBox.Show(
                this,
                $"この案件はすでにGPTへ登録されています。\n\nSupport ID: {caseRecord.SupportNumber}\n登録先: {registration.TargetGptDisplayName}\n登録日時: {registeredAt}\n\n既存のGPT案件チャットを開きますか？",
                "GPT登録済み",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (open == MessageBoxResult.Yes)
            {
                await OpenRegisteredGptAsync(caseRecord, target);
            }

            return;
        }

        if (string.Equals(registration.RegistrationState, GptRegistrationStates.NeedsRelink, StringComparison.Ordinal))
        {
            PromptRelink(caseRecord, productName, target);
            return;
        }

        var brief = BuildGptHandoffBrief(caseRecord, productName);
        var preview = new GptRegistrationPreviewDialog(
            target.DisplayName,
            caseRecord.SupportNumber,
            caseRecord.FolderName,
            brief)
        {
            Owner = this,
        };
        if (preview.ShowDialog() != true)
        {
            return;
        }

        if (preview.SelectedAction == GptRegistrationPreviewAction.LinkExisting)
        {
            LinkExistingGptConversation(caseRecord, productName, target);
            return;
        }

        if (preview.SelectedAction != GptRegistrationPreviewAction.CreateNew)
        {
            return;
        }

        _viewModel.StatusMessage = $"{target.DisplayName}へGPT案件チャットを登録しています...";
        var update = await _gptCaseRegistrationService.RegisterNewAsync(
            caseRecord,
            productName,
            target,
            preview.ApprovedBrief);
        var registrationPersisted = update.Registration is null || SaveGptRegistration(caseRecord, update.Registration);
        if (!registrationPersisted)
        {
            MessageBox.Show(
                this,
                "GPT登録情報を案件metadataへ保存できませんでした。新しいチャットを作り直さず、既存チャットを確認して再紐付けしてください。",
                "GPT登録情報の保存失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (update.Status == GptRegistrationUpdateStatus.Registered)
        {
            _viewModel.StatusMessage = update.Message;
            MessageBox.Show(this, update.Message, "GPT登録", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (update.Status == GptRegistrationUpdateStatus.NeedsRelink)
        {
            _viewModel.StatusMessage = "GPT登録は再紐付け待ちです。";
            var relink = MessageBox.Show(
                this,
                update.Message + Environment.NewLine + Environment.NewLine + "今すぐ既存チャットを再紐付けしますか？",
                "GPT再紐付けが必要です",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (relink == MessageBoxResult.Yes)
            {
                PromptRelink(caseRecord, productName, target);
            }
        }
        else
        {
            MessageBox.Show(this, update.Message, "GPT登録", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool TryGetGptRegistrationContext(
        out CaseRecord caseRecord,
        out string productName,
        out ProductGptTarget target)
    {
        caseRecord = _currentCase!;
        productName = _activeProduct?.Name?.Trim() ?? string.Empty;
        target = new ProductGptTarget(string.Empty, string.Empty, string.Empty);
        if (_currentCase is null)
        {
            MessageBox.Show(this, "案件を選択してください。", "GPT登録", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        caseRecord = _currentCase;
        if (string.IsNullOrWhiteSpace(caseRecord.SupportNumber))
        {
            MessageBox.Show(this, "Support IDがありません。", "GPT登録", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(productName))
        {
            MessageBox.Show(this, "製品が設定されていません。", "GPT登録", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!TryNormalizeCaseFolder(caseRecord.FolderPath, GetConfiguredCaseRoots(), out _))
        {
            MessageBox.Show(this, "案件フォルダを安全に確認できません。", "GPT登録", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!_productGptTargetResolver.TryResolve(_activeProduct, out target, out var error))
        {
            MessageBox.Show(this, error, "GPT登録", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private async Task OpenRegisteredGptAsync(CaseRecord caseRecord, ProductGptTarget target)
    {
        var registration = caseRecord.GptRegistration;
        if (!GptConversationUrl.TryValidateConversation(registration.ConversationUrl, out _))
        {
            var relink = MessageBox.Show(
                this,
                "この案件にはGPT登録情報がありますが、登録済みチャットURLを確認できません。再紐付けしますか？",
                "GPT登録情報",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (relink == MessageBoxResult.Yes)
            {
                PromptRelink(caseRecord, _activeProduct?.Name ?? string.Empty, target);
            }

            return;
        }

        try
        {
            await _gptCaseRegistrationService.OpenAsync(registration.ConversationUrl);
            _viewModel.StatusMessage = "登録済みGPT案件チャットを開きました。";
        }
        catch (Exception)
        {
            var relink = MessageBox.Show(
                this,
                "登録済みGPTチャットを開けませんでした。再紐付けしますか？",
                "GPT登録情報",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (relink == MessageBoxResult.Yes)
            {
                PromptRelink(caseRecord, _activeProduct?.Name ?? string.Empty, target);
            }
        }
    }

    private void PromptRelink(CaseRecord caseRecord, string productName, ProductGptTarget target)
    {
        var dialog = new GptConversationLinkDialog(caseRecord.GptRegistration.ConversationUrl)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var update = _gptCaseRegistrationService.LinkExisting(
            caseRecord,
            productName,
            target,
            dialog.ConversationUrl);
        if (update.Status != GptRegistrationUpdateStatus.Registered || update.Registration is null)
        {
            MessageBox.Show(this, update.Message, "GPT紐付け", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!SaveGptRegistration(caseRecord, update.Registration))
        {
            MessageBox.Show(this, "GPT登録情報を案件metadataへ保存できませんでした。", "GPT紐付け", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _viewModel.StatusMessage = update.Message;
    }

    private void LinkExistingGptConversation(CaseRecord caseRecord, string productName, ProductGptTarget target) =>
        PromptRelink(caseRecord, productName, target);

    private bool SaveGptRegistration(CaseRecord caseRecord, GptCaseRegistration registration)
    {
        var previous = caseRecord.GptRegistration.Clone();
        caseRecord.GptRegistration = registration.Clone();
        if (!_repository.TryUpdateCaseEntry(caseRecord))
        {
            caseRecord.GptRegistration = previous;
            UpdateGptRegistrationUi();
            return false;
        }

        _caseCache[caseRecord.FolderPath] = caseRecord;
        UpdateGptRegistrationUi();
        return true;
    }

    private string BuildGptHandoffBrief(CaseRecord caseRecord, string productName)
    {
        if (!TryNormalizeCaseFolder(caseRecord.FolderPath, GetConfiguredCaseRoots(), out var caseFolder))
        {
            throw new InvalidOperationException("案件フォルダを安全に読み込めません。");
        }

        var notes = NoteDefinitions.All.ToDictionary(
            definition => definition.Key,
            definition => ReadExistingCaseNote(caseFolder, definition, caseRecord.SupportNumber),
            StringComparer.Ordinal);
        var relatedFiles = EnumerateRelatedFileNames(caseFolder, caseRecord.SupportNumber);
        return _gptHandoffBriefBuilder.Build(new GptCaseHandoffSource(
            caseRecord,
            productName,
            notes.GetValueOrDefault("consult", string.Empty),
            notes.GetValueOrDefault("reply", string.Empty),
            notes.GetValueOrDefault("vendor", string.Empty),
            relatedFiles));
    }

    private static string ReadExistingCaseNote(
        string caseFolder,
        NoteDefinition definition,
        string supportNumber)
    {
        foreach (var fileName in definition.CandidateFileNames(supportNumber))
        {
            var path = Path.Combine(caseFolder, fileName);
            if (File.Exists(path))
            {
                return EncodingPolicy.DecodeNoteText(File.ReadAllBytes(path));
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> EnumerateRelatedFileNames(string caseFolder, string supportNumber)
    {
        try
        {
            var noteNames = NoteDefinitions.All
                .SelectMany(definition => definition.CandidateFileNames(supportNumber))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFiles(caseFolder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetFileName(path) ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name)
                    && !noteNames.Contains(name)
                    && !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .Take(20)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private void UpdateGptRegistrationUi()
    {
        if (GptRegistrationStatusText is null || GptRegistrationButton is null)
        {
            return;
        }

        var registration = _currentCase?.GptRegistration;
        var state = registration?.RegistrationState ?? GptRegistrationStates.Unregistered;
        GptRegistrationStatusText.Text = state switch
        {
            GptRegistrationStates.Registered => "登録済み",
            GptRegistrationStates.NeedsRelink => "再紐付け待ち",
            _ => "未登録",
        };
        GptRegistrationButton.Content = state switch
        {
            GptRegistrationStates.Registered => "GPTを開く",
            GptRegistrationStates.NeedsRelink => "再紐付け",
            _ => "GPTへ登録",
        };
        GptRegistrationButton.IsEnabled = _currentCase is not null && !_isGptRegistrationInProgress;
    }

    private static bool HasGptRegistrationMismatch(
        CaseRecord caseRecord,
        string productName,
        ProductGptTarget target)
    {
        var registration = caseRecord.GptRegistration;
        var hasRegistrationIdentity = !string.IsNullOrWhiteSpace(registration.SupportId)
            || !string.IsNullOrWhiteSpace(registration.Product)
            || !string.IsNullOrWhiteSpace(registration.TargetGptKey);
        return hasRegistrationIdentity &&
            (!string.Equals(registration.SupportId, caseRecord.SupportNumber, StringComparison.OrdinalIgnoreCase)
             || !string.Equals(registration.Product, productName, StringComparison.OrdinalIgnoreCase)
             || !string.Equals(registration.TargetGptKey, target.Key, StringComparison.OrdinalIgnoreCase));
    }
}
