using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Collections.ObjectModel;
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
    private readonly ObservableCollection<ProductEntry> _productEntries = new();
    private readonly ObservableCollection<ProductSummaryEntry> _productSummaries = new();
    private readonly ObservableCollection<OpenCaseEntry> _openCases = new();
    private readonly ObservableCollection<StaleCaseEntry> _staleCases = new();
    private readonly ObservableCollection<ExcludedCaseEntry> _excludedCases = new();
    private readonly ObservableCollection<string> _statusOptions = new();
    private CaseRecord? _currentCase;
    private NoteDefinition _currentNote = NoteDefinitions.All[0];
    private bool _suppressTemplateDialog;
    private ComboBoxItem? _pendingTemplateDialogItem;
    private bool _templateDialogQueued;
    private bool _suppressMainTabChange;
    private ProductProfile? _activeProduct;
    private TabItem? _settingsTabItem;
    private TabItem? _statusTabItem;
    private const int StaleDaysThreshold = 7;
    private bool _isRefreshingStatusTab;

    public ObservableCollection<string> StatusOptions => _statusOptions;

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
        InitializeProductSettings();
        InitializeStatusTab();
        InitializeMainTabs();
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

    private void InitializeProductSettings()
    {
        _productEntries.Clear();
        foreach (var product in _settings.Products)
        {
            _productEntries.Add(new ProductEntry
            {
                Name = product.Name,
                BasePath = product.BasePath,
            });
        }

        if (ProductGrid != null)
        {
            ProductGrid.ItemsSource = _productEntries;
        }
    }

    private void InitializeStatusTab()
    {
        if (StatusSummaryGrid != null)
        {
            StatusSummaryGrid.ItemsSource = _productSummaries;
        }

        if (OpenCaseGrid != null)
        {
            OpenCaseGrid.ItemsSource = _openCases;
        }

        if (StaleCaseGrid != null)
        {
            StaleCaseGrid.ItemsSource = _staleCases;
        }

        if (ExcludedCaseGrid != null)
        {
            ExcludedCaseGrid.ItemsSource = _excludedCases;
        }
    }

    private void InitializeMainTabs()
    {
        if (MainTabControl == null)
        {
            return;
        }

        _suppressMainTabChange = true;
        try
        {
            MainTabControl.Items.Clear();
            var products = _settings.Products;
            if (products.Count == 0)
            {
                SetBasePathEditingEnabled(true);
                _activeProduct = null;
                var defaultTab = new TabItem { Header = "案件", Tag = "default" };
                MainTabControl.Items.Add(defaultTab);
                _statusTabItem = new TabItem { Header = "ステータス", Tag = "status" };
                MainTabControl.Items.Add(_statusTabItem);
                _settingsTabItem = new TabItem { Header = "設定", Tag = "settings" };
                MainTabControl.Items.Add(_settingsTabItem);
                MainTabControl.SelectedItem = defaultTab;
                return;
            }

            foreach (var product in products)
            {
                var tab = new TabItem
                {
                    Header = product.Name,
                    Tag = product,
                };
                MainTabControl.Items.Add(tab);
            }

            _statusTabItem = new TabItem { Header = "ステータス", Tag = "status" };
            MainTabControl.Items.Add(_statusTabItem);
            _settingsTabItem = new TabItem { Header = "設定", Tag = "settings" };
            MainTabControl.Items.Add(_settingsTabItem);

            var activeName = _settings.ActiveProduct;
            var activeTab = MainTabControl.Items.OfType<TabItem>()
                .FirstOrDefault(item => item.Tag is ProductProfile profile &&
                                        string.Equals(profile.Name, activeName, StringComparison.Ordinal));

            MainTabControl.SelectedItem = activeTab ?? MainTabControl.Items.OfType<TabItem>()
                .FirstOrDefault(item => item.Tag is ProductProfile);
        }
        finally
        {
            _suppressMainTabChange = false;
        }

        ApplySelectedMainTab();
    }

    private void OnMainTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMainTabChange)
        {
            return;
        }

        ApplySelectedMainTab();
    }

    private void ApplySelectedMainTab()
    {
        if (MainTabControl.SelectedItem is not TabItem tab)
        {
            return;
        }

        if (tab.Tag is ProductProfile product)
        {
            SettingsContentGrid.Visibility = Visibility.Collapsed;
            StatusContentGrid.Visibility = Visibility.Collapsed;
            CaseContentGrid.Visibility = Visibility.Visible;
            ActivateProduct(product);
            return;
        }

        if (tab.Tag is string tag && tag == "settings")
        {
            SettingsContentGrid.Visibility = Visibility.Visible;
            StatusContentGrid.Visibility = Visibility.Collapsed;
            CaseContentGrid.Visibility = Visibility.Collapsed;
            return;
        }

        if (tab.Tag is string statusTag && statusTag == "status")
        {
            SettingsContentGrid.Visibility = Visibility.Collapsed;
            CaseContentGrid.Visibility = Visibility.Collapsed;
            StatusContentGrid.Visibility = Visibility.Visible;
            RefreshStatusTab();
            return;
        }

        SettingsContentGrid.Visibility = Visibility.Collapsed;
        StatusContentGrid.Visibility = Visibility.Collapsed;
        CaseContentGrid.Visibility = Visibility.Visible;
        SetBasePathEditingEnabled(true);
    }
    private void ActivateProduct(ProductProfile product)
    {
        _activeProduct = product;
        _settings.ActiveProduct = product.Name;
        BasePathTextBox.Text = product.BasePath;
        SetBasePathEditingEnabled(false);
        RefreshView();
    }

    private void SetBasePathEditingEnabled(bool enabled)
    {
        BasePathTextBox.IsReadOnly = !enabled;
        BrowseButton.IsEnabled = enabled;
    }


    private void OnProductAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new ProductEditorDialog("プロダクト追加", string.Empty, BasePathTextBox.Text.Trim())
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _productEntries.Add(new ProductEntry { Name = dialog.ProductName, BasePath = dialog.BasePath });
    }

    private void OnProductRemove(object sender, RoutedEventArgs e)
    {
        if (ProductGrid.SelectedItem is ProductEntry entry)
        {
            _productEntries.Remove(entry);
        }
    }

    private void OnProductEdit(object sender, RoutedEventArgs e)
    {
        if (ProductGrid.SelectedItem is not ProductEntry entry)
        {
            MessageBox.Show(this, "編集するプロダクトを選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ProductEditorDialog("プロダクト編集", entry.Name, entry.BasePath)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        entry.Name = dialog.ProductName;
        entry.BasePath = dialog.BasePath;
        ProductGrid.Items.Refresh();
    }

    private void OnProductMoveUp(object sender, RoutedEventArgs e)
    {
        MoveProductEntry(-1);
    }

    private void OnProductMoveDown(object sender, RoutedEventArgs e)
    {
        MoveProductEntry(1);
    }

    private void MoveProductEntry(int delta)
    {
        if (ProductGrid.SelectedItem is not ProductEntry entry)
        {
            MessageBox.Show(this, "移動するプロダクトを選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var index = _productEntries.IndexOf(entry);
        if (index < 0)
        {
            return;
        }

        var targetIndex = index + delta;
        if (targetIndex < 0 || targetIndex >= _productEntries.Count)
        {
            return;
        }

        _productEntries.Move(index, targetIndex);
        ProductGrid.SelectedItem = entry;
        ProductGrid.ScrollIntoView(entry);
    }

    private void OnProductBrowse(object sender, RoutedEventArgs e)
    {
        if (ProductGrid.SelectedItem is not ProductEntry entry)
        {
            MessageBox.Show(this, "参照するプロダクトを選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "ベースフォルダを選択",
            UseDescriptionForTitle = true,
            SelectedPath = string.IsNullOrWhiteSpace(entry.BasePath) ? BasePathTextBox.Text : entry.BasePath,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        entry.BasePath = dialog.SelectedPath;
        ProductGrid.Items.Refresh();
    }

    private void OnProductAddCurrent(object sender, RoutedEventArgs e)
    {
        var basePath = BasePathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(basePath))
        {
            MessageBox.Show(this, "現在のベースフォルダが未設定です。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new ProductEditorDialog("プロダクト追加", string.Empty, basePath)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _productEntries.Add(new ProductEntry { Name = dialog.ProductName, BasePath = dialog.BasePath });
    }

    private void OnProductSave(object sender, RoutedEventArgs e)
    {
        ProductGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var cleaned = _productEntries
            .Select(entry => new ProductEntry { Name = entry.Name?.Trim() ?? string.Empty, BasePath = entry.BasePath?.Trim() ?? string.Empty })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(entry.BasePath))
            .ToList();

        var duplicates = cleaned
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            MessageBox.Show(this, $"同名のプロダクトがあります: {string.Join(", ", duplicates)}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _settings.Products = cleaned
            .Select(entry => new ProductProfile { Name = entry.Name, BasePath = entry.BasePath })
            .ToList();

        if (_settings.Products.Count == 0)
        {
            _settings.ActiveProduct = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(_settings.ActiveProduct) ||
                 !_settings.Products.Any(item => item.Name == _settings.ActiveProduct))
        {
            _settings.ActiveProduct = _settings.Products[0].Name;
        }

        _config.Save(_settings);
        InitializeMainTabs();
        InitializeProductSettings();
        RefreshStatusTab();
        _viewModel.StatusMessage = "プロダクト設定を保存しました。";
    }

    private void OnStatusRefresh(object sender, RoutedEventArgs e)
    {
        RefreshStatusTab();
    }

    private void RefreshStatusTab()
    {
        _isRefreshingStatusTab = true;
        _productSummaries.Clear();
        _openCases.Clear();
        _staleCases.Clear();
        _excludedCases.Clear();

        var products = _settings.Products;
        if (products.Count == 0)
        {
            _viewModel.StatusMessage = "プロダクトが未設定です。";
            _isRefreshingStatusTab = false;
            return;
        }

        var excludedSet = new HashSet<string>(
            _settings.ExcludedCases.Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        var statusList = new List<string>();
        var statusSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var status in _settings.Statuses)
        {
            AddStatusOption(statusList, statusSet, status);
        }
        var allCasesByPath = new Dictionary<string, (string ProductName, CaseRecord Record)>(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTime.Today.AddDays(-StaleDaysThreshold);
        foreach (var product in products)
        {
            if (string.IsNullOrWhiteSpace(product.BasePath))
            {
                continue;
            }

            var repo = new CaseRepository(_logger);
            repo.SetBasePath(product.BasePath);
            var productRoot = NormalizePath(product.BasePath);
            var cases = repo.AllCases()
                .Where(record => IsPathUnderBase(productRoot, record.FolderPath))
                .ToList();

            foreach (var record in cases)
            {
                var pathKey = NormalizePath(record.FolderPath);
                if (!string.IsNullOrWhiteSpace(pathKey) && !allCasesByPath.ContainsKey(pathKey))
                {
                    allCasesByPath[pathKey] = (product.Name, record);
                }
            }

            var filtered = cases
                .Where(record => !excludedSet.Contains(NormalizePath(record.FolderPath)))
                .ToList();

            var directCases = filtered
                .Where(record => IsDirectChild(productRoot, record.FolderPath))
                .ToList();
            var openCases = directCases.Where(record => !IsClosedStatus(record.Status)).ToList();
            var staleCases = openCases.Where(record => IsStale(record, cutoff)).ToList();

            _productSummaries.Add(new ProductSummaryEntry
            {
                Name = product.Name,
                Total = directCases.Count,
                Open = openCases.Count,
                Stale = staleCases.Count,
                LatestUpdated = BuildLatestUpdated(directCases),
            });

            foreach (var record in openCases)
            {
                var displayStatus = NormalizeStatusLabel(record.Status);
                AddStatusOption(statusList, statusSet, displayStatus);

                _openCases.Add(new OpenCaseEntry
                {
                    ProductName = product.Name,
                    Company = record.Company,
                    SupportNumber = record.SupportNumber,
                    Status = displayStatus,
                    LastUpdatedDisplay = FormatDate(record.LastUpdated),
                    FolderPath = NormalizePath(record.FolderPath),
                });
            }

            foreach (var record in staleCases)
            {
                var displayStatus = NormalizeStatusLabel(record.Status);
                AddStatusOption(statusList, statusSet, displayStatus);

                _staleCases.Add(new StaleCaseEntry
                {
                    ProductName = product.Name,
                    Company = record.Company,
                    SupportNumber = record.SupportNumber,
                    Status = displayStatus,
                    LastUpdatedDisplay = FormatDate(record.LastUpdated),
                    FolderPath = NormalizePath(record.FolderPath),
                });
            }
        }

        foreach (var excluded in excludedSet)
        {
            if (string.IsNullOrWhiteSpace(excluded))
            {
                continue;
            }

            if (allCasesByPath.TryGetValue(excluded, out var info))
            {
                var displayStatus = NormalizeStatusLabel(info.Record.Status);
                AddStatusOption(statusList, statusSet, displayStatus);

                _excludedCases.Add(new ExcludedCaseEntry
                {
                    ProductName = info.ProductName,
                    Company = info.Record.Company,
                    SupportNumber = info.Record.SupportNumber,
                    Status = displayStatus,
                    LastUpdatedDisplay = FormatDate(info.Record.LastUpdated),
                    FolderPath = excluded,
                });
            }
            else
            {
                _excludedCases.Add(new ExcludedCaseEntry
                {
                    ProductName = "-",
                    Company = string.Empty,
                    SupportNumber = string.Empty,
                    Status = string.Empty,
                    LastUpdatedDisplay = string.Empty,
                    FolderPath = excluded,
                });
            }
        }

        var currentStatuses = new List<string>();
        var currentSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var status in _settings.Statuses)
        {
            AddStatusOption(currentStatuses, currentSet, status);
        }

        if (!statusList.SequenceEqual(currentStatuses))
        {
            _settings.Statuses = statusList;
            _config.Save(_settings);
            RefreshStatusOptions();
        }

        _viewModel.StatusMessage = "ステータスを更新しました。";
        _isRefreshingStatusTab = false;
    }

    private static bool IsClosedStatus(string? status)
    {
        return !string.IsNullOrWhiteSpace(status) && status.Contains("クローズ", StringComparison.Ordinal);
    }

    private static bool IsStale(CaseRecord record, DateTime cutoff)
    {
        if (!TryParseTimestamp(record.LastUpdated, out var lastUpdated))
        {
            return false;
        }

        return lastUpdated.Date <= cutoff.Date;
    }

    private static bool TryParseTimestamp(string? value, out DateTime parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var utc))
        {
            parsed = utc;
            return true;
        }

        if (DateTime.TryParse(value, out var local))
        {
            parsed = local;
            return true;
        }

        return false;
    }

    private static string FormatDate(string? value)
    {
        return TryParseTimestamp(value, out var parsed)
            ? parsed.ToString("yyyy/MM/dd")
            : value ?? string.Empty;
    }

    private static string BuildLatestUpdated(IEnumerable<CaseRecord> cases)
    {
        var latest = cases
            .Select(record => TryParseTimestamp(record.LastUpdated, out var parsed) ? parsed : (DateTime?)null)
            .Where(parsed => parsed.HasValue)
            .Select(parsed => parsed!.Value)
            .DefaultIfEmpty()
            .Max();

        return latest == default
            ? "-"
            : latest.ToString("yyyy/MM/dd");
    }

    private void OnStaleCaseOpen(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is StaleCaseEntry entry)
        {
            NavigateToCase(entry, focusStatus: true);
        }
    }

    private void OnOpenCaseOpen(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is OpenCaseEntry entry)
        {
            NavigateToCase(entry, focusStatus: true);
        }
    }

    private void OnStaleCaseStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingStatusTab)
        {
            return;
        }

        if (sender is not System.Windows.Controls.ComboBox combo)
        {
            return;
        }

        if (!combo.IsDropDownOpen && !combo.IsKeyboardFocusWithin)
        {
            return;
        }

        if (combo.DataContext is not StaleCaseEntry entry)
        {
            return;
        }

        var newStatus = combo.SelectedItem?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(newStatus))
        {
            return;
        }

        if (string.Equals(entry.Status, newStatus, StringComparison.Ordinal))
        {
            return;
        }

        var oldStatus = entry.Status;
        if (TryUpdateCaseStatus(entry.ProductName, entry.FolderPath, newStatus))
        {
            RefreshStatusTab();
        }
        else
        {
            entry.Status = oldStatus;
        }
    }

    private void OnOpenCaseStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingStatusTab)
        {
            return;
        }

        if (sender is not System.Windows.Controls.ComboBox combo)
        {
            return;
        }

        if (!combo.IsDropDownOpen && !combo.IsKeyboardFocusWithin)
        {
            return;
        }

        if (combo.DataContext is not OpenCaseEntry entry)
        {
            return;
        }

        var newStatus = combo.SelectedItem?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(newStatus))
        {
            return;
        }

        if (string.Equals(entry.Status, newStatus, StringComparison.Ordinal))
        {
            return;
        }

        var oldStatus = entry.Status;
        if (TryUpdateCaseStatus(entry.ProductName, entry.FolderPath, newStatus))
        {
            RefreshStatusTab();
        }
        else
        {
            entry.Status = oldStatus;
        }
    }

    private bool TryUpdateCaseStatus(string productName, string folderPath, string newStatus)
    {
        var product = _settings.Products.FirstOrDefault(p => string.Equals(p.Name, productName, StringComparison.Ordinal));
        if (product == null)
        {
            MessageBox.Show(this, $"プロダクト設定が見つかりません: {productName}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            MessageBox.Show(this, "案件フォルダが見つかりません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var record = CaseParser.ParseCaseFromDirectory(new DirectoryInfo(folderPath));
        if (record == null)
        {
            MessageBox.Show(this, "案件情報の解析に失敗しました。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        EnsureStatusOption(newStatus);

        var baseDir = Path.GetDirectoryName(folderPath) ?? string.Empty;
        var newName = CaseNaming.BuildFolderName(
            record.CreatedOn,
            record.Company,
            record.SupportNumber,
            newStatus,
            DateTime.Now.ToString("yyyyMMdd"));
        var newPath = Path.Combine(baseDir, newName);

        if (Directory.Exists(newPath))
        {
            MessageBox.Show(this, "同名のフォルダが既に存在します。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        try
        {
            Directory.Move(folderPath, newPath);

            var updated = new CaseRecord(
                record.Company,
                record.SupportNumber,
                newStatus,
                record.CreatedOn,
                newName,
                newPath,
                CaseNaming.ToIsoTimestamp(DateTime.UtcNow),
                GetCategoryFromPath(product.BasePath, newPath),
                false);

            _settings.RecentCases = _settings.RecentCases
                .Where(item => !string.Equals(item, folderPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _config.Save(_settings);
            _config.AddRecentCase(_settings, newPath);

            _caseCache.Remove(folderPath);
            _caseCache[newPath] = updated;

            var repo = new CaseRepository(_logger);
            repo.SetBasePath(product.BasePath);
            repo.UpdateCaseEntry(updated);

            if (_activeProduct != null && string.Equals(_activeProduct.Name, product.Name, StringComparison.Ordinal))
            {
                RefreshView();
            }

            _viewModel.StatusMessage = "ステータスを更新しました。";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ステータス更新に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void OnStaleCaseExclude(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is StaleCaseEntry entry)
        {
            var result = MessageBox.Show(this, "この案件を除外リストに追加します。よろしいですか？", "確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK)
            {
                return;
            }

            AddExcludedCase(entry.FolderPath);
        }
    }

    private void OnOpenCaseExclude(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is OpenCaseEntry entry)
        {
            var result = MessageBox.Show(this, "この案件を除外リストに追加します。よろしいですか？", "確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK)
            {
                return;
            }

            AddExcludedCase(entry.FolderPath);
        }
    }

    private void OnStaleCaseRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (StaleCaseGrid.SelectedItem is StaleCaseEntry entry)
        {
            NavigateToCase(entry, focusStatus: true);
        }
    }

    private void OnOpenCaseRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OpenCaseGrid.SelectedItem is OpenCaseEntry entry)
        {
            NavigateToCase(entry, focusStatus: true);
        }
    }

    private void NavigateToCase(StaleCaseEntry entry, bool focusStatus)
    {
        NavigateToCase(entry.ProductName, entry.FolderPath, entry.SupportNumber, focusStatus);
    }

    private void NavigateToCase(OpenCaseEntry entry, bool focusStatus)
    {
        NavigateToCase(entry.ProductName, entry.FolderPath, entry.SupportNumber, focusStatus);
    }

    private void NavigateToCase(string productName, string folderPath, string supportNumber, bool focusStatus)
    {
        var targetTab = MainTabControl.Items
            .OfType<TabItem>()
            .FirstOrDefault(item => item.Tag is ProductProfile profile &&
                                    string.Equals(profile.Name, productName, StringComparison.Ordinal));

        if (targetTab == null)
        {
            MessageBox.Show(this, $"対象プロダクトが見つかりません: {productName}", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MainTabControl.SelectedItem = targetTab;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!string.IsNullOrWhiteSpace(folderPath) && _caseCache.TryGetValue(folderPath, out var record))
            {
                SetCurrentCase(record);
                if (focusStatus)
                {
                    FocusStatusEditor();
                }
                return;
            }

            if (!string.IsNullOrWhiteSpace(supportNumber))
            {
                var found = _repository.FindBySupport(supportNumber);
                if (found != null)
                {
                    SetCurrentCase(found);
                    if (focusStatus)
                    {
                        FocusStatusEditor();
                    }
                }
            }
        }), DispatcherPriority.Background);
    }
    private void FocusStatusEditor()
    {
        StatusComboBox.Focus();
        StatusComboBox.IsDropDownOpen = true;
    }

    private void AddExcludedCase(string? folderPath)
    {
        var normalized = NormalizePath(folderPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (_settings.ExcludedCases.Any(path => string.Equals(NormalizePath(path), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _settings.ExcludedCases.Add(normalized);
        _config.Save(_settings);
        RefreshStatusTab();
    }

    private void OnExcludedCaseRestore(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is ExcludedCaseEntry entry)
        {
            RestoreExcludedCase(entry.FolderPath);
        }
    }

    private void RestoreExcludedCase(string? folderPath)
    {
        var normalized = NormalizePath(folderPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        _settings.ExcludedCases = _settings.ExcludedCases
            .Where(path => !string.Equals(NormalizePath(path), normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _config.Save(_settings);
        RefreshStatusTab();
    }

    private static string GetCategoryFromPath(string? basePath, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return string.Empty;
        }

        try
        {
            var relative = Path.GetRelativePath(basePath, targetPath);
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Length <= 1)
            {
                return string.Empty;
            }

            return segments[0];
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim();
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private static bool IsPathUnderBase(string basePath, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return false;
        }

        var target = NormalizePath(targetPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (string.Equals(target, basePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var withSeparator = basePath.EndsWith(Path.DirectorySeparatorChar)
            ? basePath
            : basePath + Path.DirectorySeparatorChar;
        return target.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStatusLabel(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未設定" : value.Trim();
    }

    private static void AddStatusOption(List<string> list, HashSet<string> set, string? value)
    {
        var normalized = NormalizeStatusLabel(value);
        if (set.Add(normalized))
        {
            list.Add(normalized);
        }
    }

    private static bool IsDirectChild(string basePath, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return false;
        }

        var target = NormalizePath(targetPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (string.Equals(target, basePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var relative = Path.GetRelativePath(basePath, target);
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            {
                return false;
            }

            return relative.IndexOf(Path.DirectorySeparatorChar) < 0
                   && relative.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        }
        catch
        {
            return false;
        }
    }

    private void LoadCategories()
    {
        CategoryComboBox.Items.Clear();
        _categoryPaths.Clear();

        var baseItem = new ComboBoxItem { Content = "(ベース直下)", Tag = string.Empty };
        CategoryComboBox.Items.Add(baseItem);

        var basePath = _repository.BasePath;
        if (string.IsNullOrWhiteSpace(basePath))
        {
            CategoryComboBox.SelectedIndex = 0;
            return;
        }

        foreach (var name in _repository.ListCategories())
        {
            var path = System.IO.Path.Combine(basePath, name);
            if (!Directory.Exists(path))
            {
                continue;
            }

            if (CaseParser.ParseCaseFromDirectory(new DirectoryInfo(path)) == null)
            {
                continue;
            }

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

        _statusOptions.Clear();
        foreach (var option in unique)
        {
            _statusOptions.Add(option);
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

    private sealed class ProductEntry
    {
        public string Name { get; set; } = string.Empty;
        public string BasePath { get; set; } = string.Empty;
    }

    private sealed class ProductSummaryEntry
    {
        public string Name { get; set; } = string.Empty;
        public int Total { get; set; }
        public int Open { get; set; }
        public int Stale { get; set; }
        public string LatestUpdated { get; set; } = "-";
    }

    private sealed class StaleCaseEntry
    {
        public string ProductName { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public string SupportNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string LastUpdatedDisplay { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
    }

    private sealed class OpenCaseEntry
    {
        public string ProductName { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public string SupportNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string LastUpdatedDisplay { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
    }

    private sealed class ExcludedCaseEntry
    {
        public string ProductName { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public string SupportNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string LastUpdatedDisplay { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
    }

    private readonly record struct TemplateEntry(string Name, string Text);
}
