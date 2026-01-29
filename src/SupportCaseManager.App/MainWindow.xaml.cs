using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using SupportCaseManager.App.Theme;
using SupportCaseManager.App.Dialogs;
using SupportCaseManager.App.ViewModels;
using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Config;
using SupportCaseManager.Core.Logging;
using SupportCaseManager.Core.Notes;
using SupportCaseManager.Core.Repository;

namespace SupportCaseManager.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ConfigStore _config;
    private readonly CaseRepository _repository;
    private readonly IAppLogger _logger;
    private readonly UserSettings _settings;
    private readonly Dictionary<string, CaseRecord> _caseCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _categoryPaths = new(StringComparer.OrdinalIgnoreCase);
    private CaseRecord? _currentCase;
    private NoteDefinition _currentNote = NoteDefinitions.All[0];
    private bool _suppressTemplateDialog;
    private ComboBoxItem? _pendingTemplateDialogItem;
    private bool _templateDialogQueued;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        _config = viewModel.Config;
        _repository = viewModel.Repository;
        _logger = viewModel.Logger;
        _settings = viewModel.Settings;

        DataContext = _viewModel;
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CreatedDatePicker.SelectedDate = DateTime.Today;
        RefreshStatusOptions();
        RefreshTemplateCombo(null);
        InitializeNoteSelector();
        TemplateComboBox.AddHandler(ComboBoxItem.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnTemplateItemClicked), true);
        StatusComboBox.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent, new TextChangedEventHandler(OnStatusTextChanged));
        UpdatePreview();
        UpdateNoteFileLabel();
        RefreshView();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PersistSettings();
        base.OnClosed(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (System.Windows.Application.Current?.MainWindow == this)
        {
            e.Cancel = true;
            Hide();
            _viewModel.StatusMessage = "トレイに格納しました。";
        }
    }

    private void OnBrowseBase(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "ベースフォルダを選択",
            UseDescriptionForTitle = true,
            SelectedPath = BasePathTextBox.Text,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            BasePathTextBox.Text = dialog.SelectedPath;
            RefreshView();
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshView();
    }

    private void OnBasePathChanged(object sender, RoutedEventArgs e)
    {
        RefreshView();
    }

    private void RefreshView()
    {
        var basePath = BasePathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(basePath))
        {
            _viewModel.StatusMessage = "ベースフォルダが未設定です。";
            return;
        }

        try
        {
            _repository.SetBasePath(basePath);
            _settings.BasePath = basePath;
            _config.Save(_settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ベースフォルダにアクセスできません: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var cases = _repository.AllCases();
        _caseCache.Clear();
        foreach (var record in cases)
        {
            _caseCache[record.FolderPath] = record;
        }

        LoadCategories();
        LoadHistory();
        _viewModel.StatusMessage = "履歴を更新しました。";
    }

    private void LoadCategories()
    {
        CategoryComboBox.Items.Clear();
        _categoryPaths.Clear();

        var baseItem = new ComboBoxItem { Content = "(ベース直下)", Tag = string.Empty };
        CategoryComboBox.Items.Add(baseItem);

        foreach (var name in _repository.ListCategories())
        {
            var item = new ComboBoxItem { Content = name, Tag = name };
            CategoryComboBox.Items.Add(item);
            _categoryPaths[name] = name;
        }

        CategoryComboBox.SelectedIndex = 0;
    }

    private void LoadHistory()
    {
        HistoryComboBox.Items.Clear();

        foreach (var path in _settings.RecentCases)
        {
            var record = _caseCache.TryGetValue(path, out var cached) ? cached : null;
            if (record == null && Directory.Exists(path))
            {
                var parsed = CaseParser.ParseCaseFromDirectory(new DirectoryInfo(path));
                if (parsed != null)
                {
                    record = parsed;
                    _caseCache[path] = parsed;
                }
            }

            var label = record?.DisplayText() ?? System.IO.Path.GetFileName(path);
            if (label.Contains("クローズ", StringComparison.Ordinal) || record?.Status == "クローズ")
            {
                continue;
            }

            HistoryComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = path });
        }

        HistoryComboBox.SelectedIndex = -1;
    }

    private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var path = item.Tag as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!_caseCache.TryGetValue(path, out var record))
        {
            var parsed = CaseParser.ParseCaseFromDirectory(new DirectoryInfo(path));
            if (parsed != null)
            {
                record = parsed;
                _caseCache[path] = parsed;
            }
        }

        if (record != null)
        {
            SetCurrentCase(record);
        }
    }

    private void OnNewCase(object sender, RoutedEventArgs e)
    {
        _currentCase = null;
        CompanyTextBox.Text = string.Empty;
        SupportTextBox.Text = string.Empty;
        CreatedDatePicker.SelectedDate = DateTime.Today;
        if (StatusComboBox.Items.Count > 0)
        {
            StatusComboBox.SelectedIndex = 0;
        }
        CategoryComboBox.SelectedIndex = 0;
        PreviewTextBox.Text = string.Empty;
        OpenFolderButton.IsEnabled = false;
        UpdateNoteFileLabel();
        UpdatePreview();
        _viewModel.StatusMessage = "新規入力モードに切り替えました。";
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_currentCase == null)
        {
            MessageBox.Show(this, "案件が選択されていません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _currentCase.FolderPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"フォルダを開けません: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        SearchAndSelect();
    }

    private void OnSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SearchAndSelect();
        }
    }

    private void SearchAndSelect()
    {
        var target = SearchTextBox.Text.Trim();
        if (string.IsNullOrEmpty(target))
        {
            MessageBox.Show(this, "サポート番号を入力してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var caseRecord = _repository.FindBySupport(target);
        if (caseRecord == null)
        {
            MessageBox.Show(this, "該当する案件が見つかりません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetCurrentCase(caseRecord);
        _viewModel.StatusMessage = "検索結果を表示しました。";
    }

    private void OnPreviewChanged(object? sender, RoutedEventArgs e)
    {
        UpdatePreview();
    }

    private void OnSupportChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePreview();
        UpdateNoteFileLabel();
    }

    private void OnStatusTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        try
        {
            var date = CreatedDatePicker.SelectedDate?.ToString("yyyyMMdd") ?? string.Empty;
            var preview = CaseNaming.BuildFolderName(
                date,
                CompanyTextBox.Text,
                SupportTextBox.Text,
                StatusComboBox.Text);
            PreviewTextBox.Text = preview;
        }
        catch
        {
            PreviewTextBox.Text = "(必須項目を入力してください)";
        }
    }

    private void RefreshStatusOptions(string? selected = null)
    {
        var options = _settings.Statuses.Count > 0 ? _settings.Statuses : Defaults.DefaultStatuses.ToList();
        var unique = options.Where(opt => !string.IsNullOrWhiteSpace(opt)).Distinct().ToList();
        if (unique.Count == 0)
        {
            unique = Defaults.DefaultStatuses.ToList();
        }

        StatusComboBox.Items.Clear();
        foreach (var option in unique)
        {
            StatusComboBox.Items.Add(option);
        }

        _settings.Statuses = unique;
        _config.Save(_settings);

        if (!string.IsNullOrWhiteSpace(selected) && unique.Contains(selected))
        {
            StatusComboBox.SelectedItem = selected;
        }
        else if (StatusComboBox.Items.Count > 0)
        {
            StatusComboBox.SelectedIndex = 0;
        }
    }

    private void OnStatusAdd(object sender, RoutedEventArgs e)
    {
        var text = StatusComboBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            MessageBox.Show(this, "ステータスを入力してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EnsureStatusOption(text);
        StatusComboBox.Text = text;
        _viewModel.StatusMessage = "ステータスを追加しました。";
    }

    private void OnStatusRemove(object sender, RoutedEventArgs e)
    {
        var current = StatusComboBox.Text.Trim();
        if (string.IsNullOrEmpty(current))
        {
            MessageBox.Show(this, "削除するステータスを選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_settings.Statuses.Count <= 1)
        {
            MessageBox.Show(this, "これ以上削除できません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _settings.Statuses = _settings.Statuses.Where(opt => opt != current).ToList();
        _config.Save(_settings);
        RefreshStatusOptions(_settings.Statuses.FirstOrDefault());
        _viewModel.StatusMessage = "ステータスを削除しました。";
    }

    private void EnsureStatusOption(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        if (_settings.Statuses.Contains(status))
        {
            return;
        }

        _settings.Statuses.Add(status);
        _config.Save(_settings);
        RefreshStatusOptions(status);
    }

    private void OnCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        UpdatePreview();

        if (CategoryComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var value = item.Tag as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var basePath = _repository.BasePath;
        if (string.IsNullOrEmpty(basePath))
        {
            return;
        }

        var targetPath = System.IO.Path.Combine(basePath, value);
        if (!Directory.Exists(targetPath))
        {
            return;
        }

        var record = CaseParser.ParseCaseFromDirectory(new DirectoryInfo(targetPath));
        if (record != null)
        {
            SetCurrentCase(record, persist: false);
        }
    }

    private void OnStatusUpdate(object sender, RoutedEventArgs e)
    {
        if (_currentCase == null)
        {
            MessageBox.Show(this, "更新する案件を選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newStatus = StatusComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newStatus))
        {
            MessageBox.Show(this, "ステータスを入力してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folder = _currentCase.FolderPath;
        var baseDir = System.IO.Path.GetDirectoryName(folder) ?? string.Empty;
        var newName = CaseNaming.BuildFolderName(
            CreatedDatePicker.SelectedDate?.ToString("yyyyMMdd") ?? string.Empty,
            CompanyTextBox.Text,
            SupportTextBox.Text,
            newStatus,
            DateTime.Now.ToString("yyyyMMdd"));
        var newPath = System.IO.Path.Combine(baseDir, newName);

        if (Directory.Exists(newPath))
        {
            MessageBox.Show(this, "同名のフォルダが既に存在します。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            Directory.Move(folder, newPath);
            var updated = new CaseRecord(
                CompanyTextBox.Text,
                SupportTextBox.Text,
                newStatus,
                CreatedDatePicker.SelectedDate?.ToString("yyyyMMdd") ?? string.Empty,
                newName,
                newPath,
                CaseNaming.ToIsoTimestamp(DateTime.UtcNow),
                GetSelectedCategoryName(),
                false);

            _currentCase = updated;
            _caseCache.Remove(folder);
            _caseCache[newPath] = updated;

            _settings.RecentCases = _settings.RecentCases
                .Where(item => !string.Equals(item, folder, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _config.Save(_settings);
            _config.AddRecentCase(_settings, newPath);

            _repository.UpdateCaseEntry(updated);
            PreviewTextBox.Text = newName;
            RefreshView();
            MessageBox.Show(this, "ステータスを更新しました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ステータス更新に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDarkModeToggled(object sender, RoutedEventArgs e)
    {
        _settings.DarkMode = DarkModeCheckBox.IsChecked ?? false;
        _config.Save(_settings);
        ThemeManager.Apply(System.Windows.Application.Current, _settings.DarkMode);
        _viewModel.StatusMessage = "ダークモード設定を保存しました。";
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.S:
                OnNoteAppend(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.O:
                OnNoteOpen(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.N:
                OnNewCase(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F:
                SearchTextBox.Focus();
                SearchTextBox.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void OnCreateCase(object sender, RoutedEventArgs e)
    {
        if (!ValidateRequired())
        {
            MessageBox.Show(this, "必須項目を入力してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EnsureStatusOption(StatusComboBox.Text);

        try
        {
            var record = _repository.CreateCase(
                CompanyTextBox.Text.Trim(),
                SupportTextBox.Text.Trim(),
                StatusComboBox.Text.Trim(),
                CreatedDatePicker.SelectedDate?.ToString("yyyyMMdd") ?? string.Empty,
                GetSelectedCategoryName(),
                OpenAfterCheckBox.IsChecked ?? false);

            SetCurrentCase(record);
            RefreshView();
            _viewModel.StatusMessage = "案件フォルダを作成しました。";
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to create case", ex);
            MessageBox.Show(this, ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool ValidateRequired()
    {
        if (string.IsNullOrWhiteSpace(BasePathTextBox.Text))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(CompanyTextBox.Text))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SupportTextBox.Text))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(StatusComboBox.Text))
        {
            return false;
        }

        return true;
    }

    private string GetSelectedCategoryName()
    {
        if (CategoryComboBox.SelectedItem is ComboBoxItem item)
        {
            var value = item.Tag as string;
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
        }

        return string.Empty;
    }

    private void InitializeNoteSelector()
    {
        NoteSelectorComboBox.Items.Clear();
        foreach (var note in NoteDefinitions.All)
        {
            NoteSelectorComboBox.Items.Add(new ComboBoxItem { Content = note.Label, Tag = note.Key });
        }
        NoteSelectorComboBox.SelectedIndex = 0;
    }

    private void OnNoteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NoteSelectorComboBox.SelectedItem is ComboBoxItem item)
        {
            var key = item.Tag as string;
            _currentNote = NoteDefinitions.GetByKey(key);
            UpdateNoteFileLabel();
        }
    }

    private void UpdateNoteFileLabel()
    {
        var path = GetNoteFilePath();
        NoteFileLabel.Text = string.IsNullOrEmpty(path) ? "ファイル: -" : $"ファイル: {path}";
    }

    private string GetNoteFilePath()
    {
        if (_currentCase == null)
        {
            return string.Empty;
        }

        var folder = _currentCase.FolderPath;
        foreach (var candidate in _currentNote.CandidateFileNames(_currentCase.SupportNumber))
        {
            var path = System.IO.Path.Combine(folder, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return System.IO.Path.Combine(folder, _currentNote.FileName(_currentCase.SupportNumber));
    }

    private void EnsureCaseNotes(CaseRecord record)
    {
        foreach (var definition in NoteDefinitions.All)
        {
            NoteService.EnsureNoteFile(record.FolderPath, definition, record.SupportNumber);
        }
    }

    private void OnNoteOpen(object sender, RoutedEventArgs e)
    {
        if (_currentCase == null)
        {
            MessageBox.Show(this, "案件を選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = GetNoteFilePath();
        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show(this, "ノートファイルが見つかりません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(path))
        {
            NoteService.EnsureNoteFile(_currentCase.FolderPath, _currentNote, _currentCase.SupportNumber);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ノートを開けません: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnNoteAppend(object sender, RoutedEventArgs e)
    {
        if (_currentCase == null)
        {
            MessageBox.Show(this, "案件を選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var body = NoteEditorTextBox.Text.Trim();
        if (string.IsNullOrEmpty(body))
        {
            MessageBox.Show(this, "追記内容を入力してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            NoteService.AppendNote(
                _currentCase.FolderPath,
                _currentNote,
                _currentCase.SupportNumber,
                StatusComboBox.Text,
                body);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"追記保存に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        NoteEditorTextBox.Text = string.Empty;
        _currentCase.LastUpdated = CaseNaming.ToIsoTimestamp(DateTime.UtcNow);
        _repository.UpdateCaseEntry(_currentCase);
        _viewModel.StatusMessage = "ノートを追記しました。";
    }

    private void OnNoteClear(object sender, RoutedEventArgs e)
    {
        NoteEditorTextBox.Clear();
        _viewModel.StatusMessage = "入力内容をクリアしました。";
    }

    private void OnNoteSubfolder(object sender, RoutedEventArgs e)
    {
        if (_currentCase == null)
        {
            MessageBox.Show(this, "案件を選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var folder = NoteService.CreateSubfolder(_currentCase.FolderPath, _currentNote, _currentCase.SupportNumber);
            MessageBox.Show(this, $"サブフォルダを作成しました:\n{folder}", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"サブフォルダ作成に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetCurrentCase(CaseRecord record, bool persist = true)
    {
        _currentCase = record;
        CompanyTextBox.Text = record.Company;
        SupportTextBox.Text = record.SupportNumber;
        EnsureStatusOption(record.Status);
        StatusComboBox.Text = record.Status;
        if (DateTime.TryParseExact(record.CreatedOn, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            CreatedDatePicker.SelectedDate = date;
        }
        PreviewTextBox.Text = record.FolderName;
        OpenFolderButton.IsEnabled = true;

        if (!string.IsNullOrEmpty(record.Category))
        {
            SelectCategory(record.Category);
        }
        else
        {
            CategoryComboBox.SelectedIndex = 0;
        }

        _caseCache[record.FolderPath] = record;

        if (persist)
        {
            _config.AddRecentCase(_settings, record.FolderPath);
            LoadHistory();
        }

        EnsureCaseNotes(record);
        UpdateNoteFileLabel();
    }

    private void SelectCategory(string category)
    {
        foreach (var item in CategoryComboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string value && string.Equals(value, category, StringComparison.OrdinalIgnoreCase))
            {
                CategoryComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void RefreshTemplateCombo(string? selectedName)
    {
        _suppressTemplateDialog = true;
        try
        {
            TemplateComboBox.Items.Clear();
            foreach (var entry in _settings.NoteTemplates)
            {
                if (!entry.TryGetValue("name", out var name))
                {
                    continue;
                }

                var item = new ComboBoxItem { Content = name, Tag = entry };
                TemplateComboBox.Items.Add(item);
            }

            if (!string.IsNullOrEmpty(selectedName))
            {
                foreach (var item in TemplateComboBox.Items.OfType<ComboBoxItem>())
                {
                    if (string.Equals(item.Content?.ToString(), selectedName, StringComparison.Ordinal))
                    {
                        TemplateComboBox.SelectedItem = item;
                        return;
                    }
                }
            }
            else if (TemplateComboBox.Items.Count > 0)
            {
                TemplateComboBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _suppressTemplateDialog = false;
        }
    }

    private void OnTemplateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTemplateDialog)
        {
            return;
        }

        if (TemplateComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _pendingTemplateDialogItem = item;

        if (!TemplateComboBox.IsDropDownOpen)
        {
            QueueTemplateDialog();
        }
    }

    private void OnTemplateItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (_suppressTemplateDialog)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var item = FindAncestor<ComboBoxItem>(source);
        if (item == null)
        {
            return;
        }

        TemplateComboBox.SelectedItem = item;
        _pendingTemplateDialogItem = item;
        TemplateComboBox.IsDropDownOpen = false;
        QueueTemplateDialog();
    }

    private void OnTemplateDropDownClosed(object sender, EventArgs e)
    {
        if (_suppressTemplateDialog)
        {
            return;
        }

        QueueTemplateDialog();
    }

    private void QueueTemplateDialog()
    {
        if (_templateDialogQueued)
        {
            return;
        }

        _templateDialogQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _templateDialogQueued = false;
            ShowPendingTemplateDialog();
        }), DispatcherPriority.Background);
    }

    private void ShowPendingTemplateDialog()
    {
        if (_pendingTemplateDialogItem == null)
        {
            return;
        }

        var item = _pendingTemplateDialogItem;
        _pendingTemplateDialogItem = null;
        ShowTemplateDialog(item);
    }

    private void ShowTemplateDialog(ComboBoxItem item)
    {
        if (item.Tag is not Dictionary<string, string> entry)
        {
            return;
        }

        var name = entry.TryGetValue("name", out var n) ? n : "テンプレート";
        var text = entry.TryGetValue("text", out var t) ? t : string.Empty;
        var dialog = new TemplateViewerDialog(
            name,
            text,
            UpdateTemplateEntry,
            RenameTemplateEntry,
            DeleteTemplateEntry);
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T target)
            {
                return target;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnTemplateSave(object sender, RoutedEventArgs e)
    {
        var content = NoteEditorTextBox.Text.Trim();
        if (string.IsNullOrEmpty(content))
        {
            MessageBox.Show(this, "テンプレートとして保存する内容を入力してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var defaultName = NoteSelectorComboBox.SelectedItem is ComboBoxItem noteItem
            ? noteItem.Content?.ToString()
            : NoteSelectorComboBox.Text;
        var dialog = new InputDialog("テンプレート保存", "テンプレート名を入力してください:", defaultName);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var name = dialog.Value.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "テンプレート名を入力してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UpdateTemplateEntry(name, content);
        RefreshTemplateCombo(name);
        MessageBox.Show(this, "テンプレートを保存しました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnTemplateImport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON ファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
            Title = "テンプレートをインポート",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(dialog.FileName, SupportCaseManager.Core.Compatibility.EncodingPolicy.Utf8NoBom);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ファイルを読み込めません: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var templates = ParseTemplatesFromJson(json, out var error);
        if (!string.IsNullOrEmpty(error))
        {
            MessageBox.Show(this, error, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (templates.Count == 0)
        {
            MessageBox.Show(this, "インポートできるテンプレートが見つかりません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var existing = _settings.NoteTemplates ?? new List<Dictionary<string, string>>();
        var existingByName = existing
            .Where(entry => entry.TryGetValue("name", out var n) && !string.IsNullOrWhiteSpace(n))
            .ToDictionary(entry => entry["name"], entry => entry, StringComparer.Ordinal);

        var duplicateCount = templates.Count(t => existingByName.ContainsKey(t.Name));
        var overwrite = false;
        if (duplicateCount > 0)
        {
            var result = MessageBox.Show(this, $"同名のテンプレートが {duplicateCount} 件あります。上書きしますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
            overwrite = result == MessageBoxResult.Yes;
        }

        var added = 0;
        var updated = 0;
        var skipped = 0;
        string? lastImported = null;
        foreach (var template in templates)
        {
            if (existingByName.TryGetValue(template.Name, out var current))
            {
                if (!overwrite)
                {
                    skipped++;
                    continue;
                }

                current["text"] = template.Text;
                updated++;
            }
            else
            {
                var entry = new Dictionary<string, string>
                {
                    ["name"] = template.Name,
                    ["text"] = template.Text,
                };
                existing.Add(entry);
                existingByName[template.Name] = entry;
                added++;
            }

            lastImported = template.Name;
        }

        _settings.NoteTemplates = existing;
        _config.Save(_settings);
        RefreshTemplateCombo(lastImported);
        _viewModel.StatusMessage = "テンプレートをインポートしました。";
        MessageBox.Show(this, $"追加: {added} / 更新: {updated} / スキップ: {skipped}", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnTemplateExport(object sender, RoutedEventArgs e)
    {
        var templates = NormalizeTemplates(_settings.NoteTemplates);
        if (templates.Count == 0)
        {
            MessageBox.Show(this, "エクスポートできるテンプレートがありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON ファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
            Title = "テンプレートをエクスポート",
            FileName = $"note-templates-{DateTime.Now:yyyyMMdd}.json",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["NoteTemplates"] = templates,
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        try
        {
            var json = JsonSerializer.Serialize(payload, options);
            File.WriteAllText(dialog.FileName, json, SupportCaseManager.Core.Compatibility.EncodingPolicy.Utf8NoBom);
            _viewModel.StatusMessage = "テンプレートをエクスポートしました。";
            MessageBox.Show(this, "テンプレートをエクスポートしました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"エクスポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateTemplateEntry(string name, string text)
    {
        var templates = _settings.NoteTemplates;
        var existing = templates.FirstOrDefault(entry => entry.TryGetValue("name", out var n) && n == name);
        if (existing != null)
        {
            existing["text"] = text;
        }
        else
        {
            templates.Add(new Dictionary<string, string>
            {
                ["name"] = name,
                ["text"] = text,
            });
        }

        _settings.NoteTemplates = templates;
        _config.Save(_settings);
        RefreshTemplateCombo(name);
        _viewModel.StatusMessage = "テンプレートを更新しました。";
    }

    private void RenameTemplateEntry(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            MessageBox.Show(this, "テンプレート名を入力してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_settings.NoteTemplates.Any(entry => entry.TryGetValue("name", out var n) && n == newName))
        {
            MessageBox.Show(this, "同名のテンプレートが存在します。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var entry in _settings.NoteTemplates)
        {
            if (entry.TryGetValue("name", out var name) && name == oldName)
            {
                entry["name"] = newName;
                break;
            }
        }

        _config.Save(_settings);
        RefreshTemplateCombo(newName);
        _viewModel.StatusMessage = "テンプレート名を変更しました。";
    }

    private void DeleteTemplateEntry(string name)
    {
        _settings.NoteTemplates = _settings.NoteTemplates
            .Where(entry => !(entry.TryGetValue("name", out var n) && n == name))
            .ToList();
        _config.Save(_settings);
        RefreshTemplateCombo(null);
        _viewModel.StatusMessage = "テンプレートを削除しました。";
    }

    private void OnTemplateMoveUp(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem menuItem && menuItem.Tag is string name)
        {
            MoveTemplateOrNotify(name, -1);
        }
    }

    private void OnTemplateMoveDown(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem menuItem && menuItem.Tag is string name)
        {
            MoveTemplateOrNotify(name, 1);
        }
    }

    private void OnTemplateItemRightClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var item = FindAncestor<ComboBoxItem>(source);
        if (item == null)
        {
            return;
        }

        var name = item.Content?.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu();
        var upItem = new System.Windows.Controls.MenuItem { Header = "上へ", Tag = name };
        var downItem = new System.Windows.Controls.MenuItem { Header = "下へ", Tag = name };
        upItem.Click += OnTemplateMoveUp;
        downItem.Click += OnTemplateMoveDown;
        menu.Items.Add(upItem);
        menu.Items.Add(downItem);
        menu.PlacementTarget = item;
        item.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void MoveTemplateOrNotify(string name, int delta)
    {
        if (!MoveTemplateEntry(name, delta, keepDropDownOpen: true))
        {
            var direction = delta < 0 ? "上" : "下";
            MessageBox.Show(this, $"これ以上{direction}に移動できません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private bool MoveTemplateEntry(string name, int delta, bool keepDropDownOpen)
    {
        var templates = _settings.NoteTemplates ?? new List<Dictionary<string, string>>();
        if (templates.Count == 0)
        {
            return false;
        }

        var index = templates.FindIndex(entry => entry.TryGetValue("name", out var n) && n == name);
        if (index < 0)
        {
            return false;
        }

        var targetIndex = index + delta;
        if (targetIndex < 0 || targetIndex >= templates.Count)
        {
            return false;
        }

        _pendingTemplateDialogItem = null;
        _templateDialogQueued = false;
        (templates[index], templates[targetIndex]) = (templates[targetIndex], templates[index]);
        _settings.NoteTemplates = templates;
        _config.Save(_settings);
        RefreshTemplateCombo(name);
        _viewModel.StatusMessage = "テンプレート順を変更しました。";
        if (keepDropDownOpen)
        {
            TemplateComboBox.IsDropDownOpen = true;
        }
        return true;
    }

    private static List<TemplateEntry> ParseTemplatesFromJson(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return ParseTemplateArray(root);
            }

            if (root.ValueKind == JsonValueKind.Object && TryGetTemplateArray(root, out var array))
            {
                return ParseTemplateArray(array);
            }

            error = "テンプレート配列が見つかりませんでした。";
            return new List<TemplateEntry>();
        }
        catch (JsonException ex)
        {
            error = $"JSON の解析に失敗しました: {ex.Message}";
            return new List<TemplateEntry>();
        }
    }

    private static bool TryGetTemplateArray(JsonElement root, out JsonElement array)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            if (string.Equals(prop.Name, "NoteTemplates", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prop.Name, "Templates", StringComparison.OrdinalIgnoreCase))
            {
                array = prop.Value;
                return true;
            }
        }

        array = default;
        return false;
    }

    private static List<TemplateEntry> ParseTemplateArray(JsonElement array)
    {
        var list = new List<TemplateEntry>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in item.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    raw[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }

            if (!TryExtractTemplate(raw, out var name, out var text))
            {
                continue;
            }

            list.Add(new TemplateEntry(name, text));
        }

        return list;
    }

    private static bool TryExtractTemplate(Dictionary<string, string> raw, out string name, out string text)
    {
        name = GetValue(raw, "name") ?? string.Empty;
        text = GetValue(raw, "text") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return true;
    }

    private static string? GetValue(Dictionary<string, string> raw, string key)
    {
        foreach (var entry in raw)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static List<Dictionary<string, string>> NormalizeTemplates(IEnumerable<Dictionary<string, string>> templates)
    {
        var list = new List<Dictionary<string, string>>();
        foreach (var template in templates)
        {
            if (!TryExtractTemplate(template, out var name, out var text))
            {
                continue;
            }

            list.Add(new Dictionary<string, string>
            {
                ["name"] = name,
                ["text"] = text,
            });
        }

        return list;
    }

    private readonly record struct TemplateEntry(string Name, string Text);
}
