using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using SupportCaseManager.App.AiHandoff;
using SupportCaseManager.App.Theme;
using SupportCaseManager.App.Dialogs;
using SupportCaseManager.App.ViewModels;
using SupportCaseManager.Core.Cases;
using SupportCaseManager.Core.Compatibility;
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
    private readonly IAiAssistantLaunchContextBuilder _aiLaunchContextBuilder = new AiAssistantLaunchContextBuilder();
    private readonly IAiAssistantHandoffFileWriter _aiHandoffFileWriter = new AiAssistantHandoffFileWriter();
    private readonly IAiAssistantProcessLauncher _aiProcessLauncher = new AiAssistantProcessLauncher();
    private readonly Dictionary<string, CaseRecord> _caseCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _categoryPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ProductEntry> _productEntries = new();
    private readonly ObservableCollection<ProductSummaryEntry> _productSummaries = new();
    private readonly ObservableCollection<string> _productNameOptions = new();
    private readonly ObservableCollection<OpenCaseEntry> _openCases = new();
    private readonly ObservableCollection<StaleCaseEntry> _staleCases = new();
    private readonly ObservableCollection<ExcludedCaseEntry> _excludedCases = new();
    private readonly ObservableCollection<ClosedCaseEntry> _closedBaseCases = new();
    private readonly ObservableCollection<ClosedCaseEntry> _closedFolderCases = new();
    private readonly ObservableCollection<ClosedSummaryEntry> _closedSummaries = new();
    private readonly ObservableCollection<ClosedMonthlySummaryEntry> _closedMonthlySummaries = new();
    private readonly List<ClosedCaseEntry> _closedBaseCasesSource = new();
    private readonly List<ClosedCaseEntry> _closedFolderCasesSource = new();
    private readonly List<ClosedCaseEntry> _closedSummaryEntries = new();
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
    private TabItem? _closedTabItem;
    private const int StaleDaysThreshold = 7;
    private bool _isRefreshingStatusTab;
    private bool _isStatusRefreshQueued;
    private bool _statusTabDirty = true;
    private bool _closedTabDirty = true;
    private DateTime _statusTabRefreshedAtUtc = DateTime.MinValue;
    private DateTime _closedTabRefreshedAtUtc = DateTime.MinValue;
    private int _statusRefreshVersion;
    private int _closedRefreshVersion;
    private int _caseRefreshVersion;
    private int _loadingIndicatorVersion;
    private int _closedSearchVersion;
    private int _closedSearchLoadingVersion = -1;
    private CancellationTokenSource? _statusRefreshCts;
    private CancellationTokenSource? _closedRefreshCts;
    private CancellationTokenSource? _caseRefreshCts;
    private CancellationTokenSource? _caseTabPreloadCts;
    private CancellationTokenSource? _closedSearchCts;
    private readonly Dictionary<string, DirectoryScanCacheRoot> _directoryScanCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<CaseRecord>> _caseTabCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _caseTabRefreshedAtUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _caseTabDirtyKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directoryScanCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "itoke",
        "SupportCaseManager",
        "scan-cache-v1.json");
    private bool _directoryScanCacheDirty;
    private bool _isNotePreviewActive;
    private string _notePreviewBody = string.Empty;
    private string _closedSearchKeyword = string.Empty;
    private string _closedSummaryFilterProduct = string.Empty;
    private string _closedSummaryFilterYear = string.Empty;
    private int _closedSummaryFilterMonth;
    private string _closedSummarySelectedYear = string.Empty;
    private static readonly string[] ClosedMonthLabels = new[]
    {
        "1月", "2月", "3月", "4月", "5月", "6月",
        "7月", "8月", "9月", "10月", "11月", "12月",
    };
    private static readonly TimeSpan ClosedSearchMinIndicatorDuration = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan TabRefreshCacheLifetime = TimeSpan.FromMinutes(2);

    public ObservableCollection<string> StatusOptions => _statusOptions;
    public ObservableCollection<string> ProductNameOptions => _productNameOptions;

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
        LoadDirectoryScanCache();
        CreatedDatePicker.SelectedDate = DateTime.Today;
        RefreshStatusOptions();
        RefreshTemplateCombo(null);
        InitializeProductSettings();
        InitializeStatusTab();
        InitializeClosedTab();
        InitializeMainTabs();
        InitializeNoteSelector();
        TemplateComboBox.AddHandler(ComboBoxItem.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnTemplateItemClicked), true);
        StatusComboBox.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent, new TextChangedEventHandler(OnStatusTextChanged));
        UpdatePreview();
        UpdateNoteFileLabel();
        if (_activeProduct == null)
        {
            RefreshView();
        }

        StartCaseTabPreload();
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveDirectoryScanCache();
        _statusRefreshCts?.Cancel();
        _statusRefreshCts?.Dispose();
        _statusRefreshCts = null;
        _closedRefreshCts?.Cancel();
        _closedRefreshCts?.Dispose();
        _closedRefreshCts = null;
        _caseRefreshCts?.Cancel();
        _caseRefreshCts?.Dispose();
        _caseRefreshCts = null;
        _closedSearchCts?.Cancel();
        _closedSearchCts?.Dispose();
        _closedSearchCts = null;
        _caseTabPreloadCts?.Cancel();
        _caseTabPreloadCts?.Dispose();
        _caseTabPreloadCts = null;
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
            RefreshView(force: true);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshView(force: true);
    }

    private void OnBasePathChanged(object sender, RoutedEventArgs e)
    {
        RefreshView(force: true);
    }

    private async void RefreshView(bool force = false)
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

        var normalizedBasePath = NormalizePath(basePath);
        _caseTabCache.TryGetValue(normalizedBasePath, out var cachedCases);
        var hasCachedAt = _caseTabRefreshedAtUtc.TryGetValue(normalizedBasePath, out var cachedAt);
        var shouldUseCache =
            !force &&
            cachedCases != null &&
            !_caseTabDirtyKeys.Contains(normalizedBasePath) &&
            hasCachedAt &&
            (DateTime.UtcNow - cachedAt) <= TabRefreshCacheLifetime;
        if (shouldUseCache && cachedCases != null)
        {
            _caseCache.Clear();
            foreach (var record in cachedCases)
            {
                _caseCache[record.FolderPath] = record;
            }

            LoadCategories();
            LoadHistory();
            _viewModel.StatusMessage = "履歴を更新しました。";
            return;
        }

        var requestVersion = Interlocked.Increment(ref _caseRefreshVersion);
        _caseRefreshCts?.Cancel();
        _caseRefreshCts?.Dispose();
        _caseRefreshCts = new CancellationTokenSource();
        var token = _caseRefreshCts.Token;
        var loadingVersion = StartLoading("案件タブを読み込み中...");
        IProgress<LoadProgress> progress = new Progress<LoadProgress>(item => ReportLoading(loadingVersion, item.Percent, item.Message));
        try
        {
            progress.Report(new LoadProgress(10, "案件インデックスを準備中..."));
            var cases = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var localRepo = new CaseRepository(_logger);
                localRepo.SetBasePath(basePath);
                progress.Report(new LoadProgress(25, "案件フォルダを解析中..."));
                var result = localRepo.AllCases();
                progress.Report(new LoadProgress(85, "画面に反映中..."));
                return result;
            }, token);

            if (token.IsCancellationRequested || requestVersion != _caseRefreshVersion)
            {
                return;
            }

            _repository.SetBasePath(basePath);
            _caseCache.Clear();
            foreach (var record in cases)
            {
                _caseCache[record.FolderPath] = record;
            }

            _caseTabCache[normalizedBasePath] = cases.Select(item => item.CloneWith(isFromFolder: item.IsFromFolder)).ToList();
            _caseTabRefreshedAtUtc[normalizedBasePath] = DateTime.UtcNow;
            _caseTabDirtyKeys.Remove(normalizedBasePath);

            LoadCategories();
            LoadHistory();
            progress.Report(new LoadProgress(100, "履歴を更新しました。"));
            _viewModel.StatusMessage = "履歴を更新しました。";
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to refresh case tab", ex);
            MessageBox.Show(this, $"ベースフォルダにアクセスできません: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StopLoading(loadingVersion);
        }
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
                ClosedPath = product.ClosedPath,
                NoteTemplates = NormalizeTemplates(product.NoteTemplates ?? new List<Dictionary<string, string>>()),
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
            RebuildStatusSummaryColumns(Array.Empty<string>());
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

    private void InitializeClosedTab()
    {
        if (ClosedSummaryGrid != null)
        {
            ClosedSummaryGrid.ItemsSource = _closedSummaries;
            RebuildClosedSummaryColumns(Array.Empty<string>());
        }

        if (ClosedMonthlySummaryGrid != null)
        {
            ClosedMonthlySummaryGrid.ItemsSource = _closedMonthlySummaries;
            RebuildClosedMonthlySummaryColumns();
        }

        if (ClosedBaseGrid != null)
        {
            ClosedBaseGrid.ItemsSource = _closedBaseCases;
        }

        if (ClosedFolderGrid != null)
        {
            ClosedFolderGrid.ItemsSource = _closedFolderCases;
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
                RefreshTemplateCombo(null);
                var defaultTab = new TabItem { Header = "案件", Tag = "default" };
                MainTabControl.Items.Add(defaultTab);
                _statusTabItem = new TabItem { Header = "ステータス", Tag = "status" };
                MainTabControl.Items.Add(_statusTabItem);
                _closedTabItem = new TabItem { Header = "クローズ", Tag = "closed" };
                MainTabControl.Items.Add(_closedTabItem);
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
            _closedTabItem = new TabItem { Header = "クローズ", Tag = "closed" };
            MainTabControl.Items.Add(_closedTabItem);
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

        if (MainTabControl.SelectedItem is TabItem selectedTab &&
            selectedTab.Header is string header &&
            !string.IsNullOrWhiteSpace(header))
        {
            _viewModel.StatusMessage = $"{header} タブへ切り替えました。";
        }

        ApplySelectedMainTab();
    }

    private void ApplySelectedMainTab()
    {
        if (MainTabControl.SelectedItem is not TabItem tab)
        {
            return;
        }

        var isClosedTarget = tab.Tag is string targetTag && targetTag == "closed";
        if (!isClosedTarget)
        {
            CancelClosedSearchFilter();
        }

        if (tab.Tag is ProductProfile product)
        {
            SettingsContentGrid.Visibility = Visibility.Collapsed;
            StatusContentGrid.Visibility = Visibility.Collapsed;
            ClosedContentGrid.Visibility = Visibility.Collapsed;
            CaseContentGrid.Visibility = Visibility.Visible;
            ActivateProduct(product);
            return;
        }

        if (tab.Tag is string tag && tag == "settings")
        {
            SettingsContentGrid.Visibility = Visibility.Visible;
            StatusContentGrid.Visibility = Visibility.Collapsed;
            ClosedContentGrid.Visibility = Visibility.Collapsed;
            CaseContentGrid.Visibility = Visibility.Collapsed;
            return;
        }

        if (tab.Tag is string statusTag && statusTag == "status")
        {
            SettingsContentGrid.Visibility = Visibility.Collapsed;
            CaseContentGrid.Visibility = Visibility.Collapsed;
            ClosedContentGrid.Visibility = Visibility.Collapsed;
            StatusContentGrid.Visibility = Visibility.Visible;
            EnsureStatusTabData();
            return;
        }

        if (tab.Tag is string closedTag && closedTag == "closed")
        {
            SettingsContentGrid.Visibility = Visibility.Collapsed;
            CaseContentGrid.Visibility = Visibility.Collapsed;
            StatusContentGrid.Visibility = Visibility.Collapsed;
            ClosedContentGrid.Visibility = Visibility.Visible;
            EnsureClosedTabData();
            return;
        }

        SettingsContentGrid.Visibility = Visibility.Collapsed;
        StatusContentGrid.Visibility = Visibility.Collapsed;
        ClosedContentGrid.Visibility = Visibility.Collapsed;
        CaseContentGrid.Visibility = Visibility.Visible;
        SetBasePathEditingEnabled(true);
    }
    private void ActivateProduct(ProductProfile product)
    {
        _activeProduct = product;
        _settings.ActiveProduct = product.Name;
        BasePathTextBox.Text = product.BasePath;
        SetBasePathEditingEnabled(false);
        EnsureActiveProductTemplates();
        RefreshTemplateCombo(null);
        RefreshView();
    }

    private void SetBasePathEditingEnabled(bool enabled)
    {
        BasePathTextBox.IsReadOnly = !enabled;
        BrowseButton.IsEnabled = enabled;
    }


    private void OnProductAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new ProductEditorDialog("プロダクト追加", string.Empty, BasePathTextBox.Text.Trim(), string.Empty)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _productEntries.Add(new ProductEntry
        {
            Name = dialog.ProductName,
            BasePath = dialog.BasePath,
            ClosedPath = dialog.ClosedPath,
            NoteTemplates = new List<Dictionary<string, string>>(),
        });
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

        var dialog = new ProductEditorDialog("プロダクト編集", entry.Name, entry.BasePath, entry.ClosedPath)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        entry.Name = dialog.ProductName;
        entry.BasePath = dialog.BasePath;
        entry.ClosedPath = dialog.ClosedPath;
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

        var dialog = new ProductEditorDialog("プロダクト追加", string.Empty, basePath, string.Empty)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _productEntries.Add(new ProductEntry
        {
            Name = dialog.ProductName,
            BasePath = dialog.BasePath,
            ClosedPath = dialog.ClosedPath,
            NoteTemplates = new List<Dictionary<string, string>>(),
        });
    }

    private void OnProductSave(object sender, RoutedEventArgs e)
    {
        ProductGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var cleaned = _productEntries
            .Select(entry => new ProductEntry
            {
                Name = entry.Name?.Trim() ?? string.Empty,
                BasePath = entry.BasePath?.Trim() ?? string.Empty,
                ClosedPath = entry.ClosedPath?.Trim() ?? string.Empty,
                NoteTemplates = NormalizeTemplates(entry.NoteTemplates ?? new List<Dictionary<string, string>>()),
            })
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
            .Select(entry => new ProductProfile
            {
                Name = entry.Name,
                BasePath = entry.BasePath,
                ClosedPath = entry.ClosedPath,
                NoteTemplates = entry.NoteTemplates ?? new List<Dictionary<string, string>>(),
            })
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
        MarkAllDataDirty(refreshIfVisible: true);
        StartCaseTabPreload();
        _viewModel.StatusMessage = "プロダクト設定を保存しました。";
    }

    private void OnStatusRefresh(object sender, RoutedEventArgs e)
    {
        _statusTabDirty = true;
        EnsureStatusTabData(force: true);
    }

    private void QueueStatusTabRefresh()
    {
        _statusTabDirty = true;
        if (!IsStatusTabVisible())
        {
            return;
        }

        if (_isStatusRefreshQueued)
        {
            return;
        }

        _isStatusRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _isStatusRefreshQueued = false;
            EnsureStatusTabData(force: true);
        }), DispatcherPriority.Background);
    }

    private void OnClosedRefresh(object sender, RoutedEventArgs e)
    {
        _closedTabDirty = true;
        EnsureClosedTabData(force: true);
    }

    private bool IsStatusTabVisible()
    {
        return StatusContentGrid.Visibility == Visibility.Visible;
    }

    private bool IsClosedTabVisible()
    {
        return ClosedContentGrid.Visibility == Visibility.Visible;
    }

    private void EnsureStatusTabData(bool force = false)
    {
        if (force || _statusTabDirty || (DateTime.UtcNow - _statusTabRefreshedAtUtc) > TabRefreshCacheLifetime)
        {
            RefreshStatusTab();
        }
    }

    private void EnsureClosedTabData(bool force = false)
    {
        if (force || _closedTabDirty || (DateTime.UtcNow - _closedTabRefreshedAtUtc) > TabRefreshCacheLifetime)
        {
            RefreshClosedTab();
        }
    }

    private void CancelClosedSearchFilter()
    {
        _closedSearchCts?.Cancel();
        _closedSearchCts?.Dispose();
        _closedSearchCts = null;

        if (_closedSearchLoadingVersion >= 0)
        {
            StopLoading(_closedSearchLoadingVersion);
            _closedSearchLoadingVersion = -1;
        }
    }

    private void MarkStatusTabDirty(bool refreshIfVisible = false)
    {
        _statusTabDirty = true;
        if (refreshIfVisible && IsStatusTabVisible())
        {
            EnsureStatusTabData(force: true);
        }
    }

    private void MarkClosedTabDirty(bool refreshIfVisible = false)
    {
        _closedTabDirty = true;
        if (refreshIfVisible && IsClosedTabVisible())
        {
            EnsureClosedTabData(force: true);
        }
    }

    private void MarkStatusAndClosedTabsDirty(bool refreshIfVisible = false)
    {
        MarkStatusTabDirty(refreshIfVisible);
        MarkClosedTabDirty(refreshIfVisible);
    }

    private void MarkCaseTabDirty(string? basePath = null, bool refreshIfVisible = false)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            foreach (var key in _caseTabCache.Keys.ToList())
            {
                _caseTabDirtyKeys.Add(key);
            }
        }
        else
        {
            _caseTabDirtyKeys.Add(NormalizePath(basePath));
        }

        if (refreshIfVisible && CaseContentGrid.Visibility == Visibility.Visible)
        {
            RefreshView(force: true);
        }
    }

    private void MarkAllDataDirty(bool refreshIfVisible = false)
    {
        MarkCaseTabDirty(refreshIfVisible: refreshIfVisible);
        MarkStatusAndClosedTabsDirty(refreshIfVisible);
    }

    private void StartCaseTabPreload()
    {
        _caseTabPreloadCts?.Cancel();
        _caseTabPreloadCts?.Dispose();
        _caseTabPreloadCts = new CancellationTokenSource();
        _ = PreloadCaseTabsAsync(_caseTabPreloadCts.Token);
    }

    private async Task PreloadCaseTabsAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(1200, token);
            var products = _settings.Products
                .Where(item => !string.IsNullOrWhiteSpace(item.BasePath))
                .OrderBy(GetCasePreloadPriority)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var activeBase = NormalizePath(_activeProduct?.BasePath);
            foreach (var product in products)
            {
                token.ThrowIfCancellationRequested();

                var basePath = NormalizePath(product.BasePath);
                if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(activeBase) &&
                    string.Equals(basePath, activeBase, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var shouldPreload = await Dispatcher.InvokeAsync(() => ShouldPreloadCaseTab(basePath), DispatcherPriority.Background);
                if (!shouldPreload)
                {
                    continue;
                }

                await WaitUntilUiIdleAsync(token);

                var stopwatch = Stopwatch.StartNew();
                var cases = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var repository = new CaseRepository(_logger);
                    repository.SetBasePath(basePath);
                    return repository.AllCases();
                }, token);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _caseTabCache[basePath] = cases
                        .Select(item => item.CloneWith(isFromFolder: item.IsFromFolder))
                        .ToList();
                    _caseTabRefreshedAtUtc[basePath] = DateTime.UtcNow;
                    _caseTabDirtyKeys.Remove(basePath);
                }, DispatcherPriority.Background);

                _logger.Debug($"Preloaded case tab cache: {product.Name} ({cases.Count}件, {stopwatch.ElapsedMilliseconds}ms)");
            }
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception ex)
        {
            _logger.Warning($"案件タブのプリロードに失敗しました: {ex.Message}");
        }
    }

    private async Task WaitUntilUiIdleAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var isBusy = await Dispatcher.InvokeAsync(
                () => LoadingIndicatorPanel != null && LoadingIndicatorPanel.Visibility == Visibility.Visible,
                DispatcherPriority.Background);
            if (!isBusy)
            {
                return;
            }

            await Task.Delay(200, token);
        }
    }

    private bool ShouldPreloadCaseTab(string normalizedBasePath)
    {
        if (string.IsNullOrWhiteSpace(normalizedBasePath))
        {
            return false;
        }

        if (_caseTabDirtyKeys.Contains(normalizedBasePath))
        {
            return true;
        }

        if (!_caseTabCache.TryGetValue(normalizedBasePath, out var cached) || cached == null)
        {
            return true;
        }

        if (!_caseTabRefreshedAtUtc.TryGetValue(normalizedBasePath, out var refreshedAt))
        {
            return true;
        }

        return (DateTime.UtcNow - refreshedAt) > TabRefreshCacheLifetime;
    }

    private static int GetCasePreloadPriority(ProductProfile product)
    {
        var name = product.Name ?? string.Empty;
        if (name.Contains("checkmarx", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.Contains("helix", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("qac", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 10;
    }

    private int StartLoading(string message)
    {
        var version = Interlocked.Increment(ref _loadingIndicatorVersion);
        if (LoadingIndicatorPanel != null)
        {
            LoadingIndicatorPanel.Visibility = Visibility.Visible;
        }

        if (LoadingProgressBar != null)
        {
            // 描画直後に見えるよう、最初はインジケータを回してから
            // 進捗通知受信時に確定パーセント表示へ切り替える。
            LoadingProgressBar.IsIndeterminate = true;
            LoadingProgressBar.Value = 0;
        }

        if (LoadingPercentText != null)
        {
            LoadingPercentText.Text = "0%";
        }

        if (LoadingLabelText != null)
        {
            LoadingLabelText.Text = string.IsNullOrWhiteSpace(message) ? "読み込み中" : message;
        }

        _viewModel.StatusMessage = message;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        return version;
    }

    private void ReportLoading(int version, double percent, string? message = null)
    {
        if (version != _loadingIndicatorVersion)
        {
            return;
        }

        var normalized = Math.Max(0, Math.Min(100, percent));
        if (LoadingProgressBar != null)
        {
            LoadingProgressBar.IsIndeterminate = false;
            LoadingProgressBar.Value = normalized;
        }

        if (LoadingPercentText != null)
        {
            LoadingPercentText.Text = $"{normalized:0}%";
        }

        if (LoadingLabelText != null && !string.IsNullOrWhiteSpace(message))
        {
            LoadingLabelText.Text = message;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            _viewModel.StatusMessage = message;
        }
    }

    private void StopLoading(int version)
    {
        if (version != _loadingIndicatorVersion)
        {
            return;
        }

        if (LoadingIndicatorPanel != null)
        {
            LoadingIndicatorPanel.Visibility = Visibility.Collapsed;
        }

        Mouse.OverrideCursor = null;
    }

    private void OnClosedSearch(object sender, RoutedEventArgs e)
    {
        ApplyClosedSearchFilter(updateStatusMessage: true);
    }

    private void OnClosedSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyClosedSearchFilter(updateStatusMessage: true);
            e.Handled = true;
        }
    }

    private void OnClosedSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // IME入力やフォーカス移動中の中間イベントで再検索が暴発しないよう、
        // TextChanged ではキーワード保持のみ行い、実検索は Enter / 検索ボタン / 集計クリック時に実行する。
        _closedSearchKeyword = ClosedSearchTextBox?.Text?.Trim() ?? string.Empty;
    }

    private void OnClosedSearchClear(object sender, RoutedEventArgs e)
    {
        _closedSummaryFilterProduct = string.Empty;
        _closedSummaryFilterYear = string.Empty;
        _closedSummaryFilterMonth = 0;
        _closedSummarySelectedYear = string.Empty;

        if (ClosedSearchTextBox != null)
        {
            ClosedSearchTextBox.Clear();
        }

        ClearClosedSummarySelection();
        ClearClosedMonthlySummarySelection();

        RefreshClosedMonthlySummary();
        ApplyClosedSearchFilter(updateStatusMessage: true);
    }

    private void OnClosedSummaryCellChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        var cell = e.AddedCells.FirstOrDefault();
        if (cell.Item is not ClosedSummaryEntry row || cell.Column == null)
        {
            return;
        }

        var header = cell.Column.Header?.ToString()?.Trim() ?? string.Empty;
        ApplyClosedSummaryCellFilter(row, header);
    }

    private void OnClosedSummaryHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (ClosedSummaryGrid == null)
        {
            return;
        }

        if (TryGetClickedDataGridCell(ClosedSummaryGrid, e, out ClosedSummaryEntry? row, out var cellHeader) &&
            row != null &&
            ApplyClosedSummaryCellFilter(row, cellHeader))
        {
            e.Handled = true;
        }
    }

    private void OnClosedSummarySorting(object sender, DataGridSortingEventArgs e)
    {
        // 年ヘッダクリック起点のフィルタ挙動を無効化し、
        // セル（件数）クリックのみで集計フィルタをかける。
        e.Handled = true;
    }

    private bool TryApplyClosedSummaryHeaderFilter(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        if (IsYearSegment(header))
        {
            _closedSummarySelectedYear = header;
            _closedSummaryFilterYear = header;
            _closedSummaryFilterProduct = string.Empty;
            _closedSummaryFilterMonth = 0;
            ClearClosedSummarySelection();
            ClearClosedMonthlySummarySelection();
            RefreshClosedMonthlySummary();
            ApplyClosedSearchFilter(updateStatusMessage: true);
            return true;
        }

        if (string.Equals(header, "年不明", StringComparison.Ordinal))
        {
            _closedSummarySelectedYear = string.Empty;
            _closedSummaryFilterYear = header;
            _closedSummaryFilterProduct = string.Empty;
            _closedSummaryFilterMonth = 0;
            ClearClosedSummarySelection();
            ClearClosedMonthlySummarySelection();
            RefreshClosedMonthlySummary();
            ApplyClosedSearchFilter(updateStatusMessage: true);
            return true;
        }

        return false;
    }

    private void ClearClosedSummarySelection()
    {
        if (ClosedSummaryGrid == null)
        {
            return;
        }

        ClosedSummaryGrid.SelectedCells.Clear();
        ClosedSummaryGrid.SelectedItem = null;
    }

    private void ClearClosedMonthlySummarySelection()
    {
        if (ClosedMonthlySummaryGrid == null)
        {
            return;
        }

        ClosedMonthlySummaryGrid.SelectedCells.Clear();
        ClosedMonthlySummaryGrid.SelectedItem = null;
    }

    private void OnClosedMonthlySummaryCellChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        var cell = e.AddedCells.FirstOrDefault();
        if (cell.Item is not ClosedMonthlySummaryEntry row || cell.Column == null)
        {
            return;
        }

        var header = cell.Column.Header?.ToString()?.Trim() ?? string.Empty;
        ApplyClosedMonthlySummaryCellFilter(row, header);
    }

    private void OnClosedMonthlySummaryClick(object sender, MouseButtonEventArgs e)
    {
        if (ClosedMonthlySummaryGrid == null)
        {
            return;
        }

        if (!TryGetClickedDataGridCell(ClosedMonthlySummaryGrid, e, out ClosedMonthlySummaryEntry? row, out var header) ||
            row == null)
        {
            return;
        }

        if (ApplyClosedMonthlySummaryCellFilter(row, header))
        {
            e.Handled = true;
        }
    }

    private static bool TryGetClickedDataGridCell<TItem>(
        System.Windows.Controls.DataGrid grid,
        MouseButtonEventArgs e,
        out TItem? row,
        out string header)
        where TItem : class
    {
        row = null;
        header = string.Empty;

        var source = e.OriginalSource as DependencyObject;
        var cell = FindVisualParent<System.Windows.Controls.DataGridCell>(source);
        if (cell == null)
        {
            var hit = grid.InputHitTest(e.GetPosition(grid)) as DependencyObject;
            cell = FindVisualParent<System.Windows.Controls.DataGridCell>(hit);
        }

        if (cell?.DataContext is TItem cellRow && cell.Column != null)
        {
            row = cellRow;
            header = cell.Column.Header?.ToString()?.Trim() ?? string.Empty;
            return true;
        }

        return false;
    }

    private bool ApplyClosedSummaryCellFilter(ClosedSummaryEntry row, string header)
    {
        // 誤クリックでフィルタ解除にならないよう、件数列のうち「年」列のみ反応させる。
        if (!IsYearSegment(header) && !string.Equals(header, "年不明", StringComparison.Ordinal))
        {
            return false;
        }

        var nextProduct = row.IsGrandTotal ? string.Empty : row.ProductName;
        var nextYear = header;
        var selectedYear = IsYearSegment(header) ? header : string.Empty;

        _closedSummaryFilterProduct = nextProduct;
        _closedSummaryFilterYear = nextYear;
        _closedSummaryFilterMonth = 0;
        _closedSummarySelectedYear = selectedYear;

        ResetClosedSummarySelectionState();
        RefreshClosedMonthlySummary();
        ApplyClosedSearchFilter(updateStatusMessage: true);
        return true;
    }

    private bool ApplyClosedMonthlySummaryCellFilter(ClosedMonthlySummaryEntry row, string header)
    {
        if (string.IsNullOrWhiteSpace(_closedSummarySelectedYear) || !IsYearSegment(_closedSummarySelectedYear))
        {
            return false;
        }

        var nextProduct = row.IsGrandTotal ? string.Empty : row.ProductName;
        if (!TryParseMonthLabel(header, out var nextMonth))
        {
            return false;
        }

        _closedSummaryFilterProduct = nextProduct;
        _closedSummaryFilterYear = _closedSummarySelectedYear;
        _closedSummaryFilterMonth = nextMonth;

        ResetClosedSummarySelectionState();
        ApplyClosedSearchFilter(updateStatusMessage: true);
        return true;
    }

    private void ResetClosedSummarySelectionState()
    {
        // 選択セルを毎回リセットし、クリック連打時の選択状態残りによる
        // フィルタ不整合を防ぐ。
        ClearClosedSummarySelection();
        ClearClosedMonthlySummarySelection();
    }

    private static T? FindVisualParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T matched)
            {
                return matched;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnClosedOrganize(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, "クローズフォルダ内の案件を年フォルダへ整理します。よろしいですか？", "確認", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        var moved = 0;
        var skipped = 0;
        var failed = 0;
        var refreshNeeded = false;

        foreach (var product in _settings.Products)
        {
            var closedRoot = NormalizePath(product.ClosedPath);
            if (string.IsNullOrWhiteSpace(closedRoot) || !Directory.Exists(closedRoot))
            {
                continue;
            }

            foreach (var record in ScanCasesUnder(closedRoot))
            {
                if (!IsClosedStatus(record.Status))
                {
                    continue;
                }

                var folderPath = NormalizePath(record.FolderPath);
                if (!IsPathUnderBase(closedRoot, folderPath))
                {
                    continue;
                }

                var expectedYear = GetYearFromCreatedOn(record.CreatedOn);
                var currentYear = GetYearFolderFromPath(closedRoot, folderPath);
                if (!string.IsNullOrWhiteSpace(currentYear) && string.Equals(currentYear, expectedYear, StringComparison.Ordinal))
                {
                    skipped++;
                    continue;
                }

                if (TryMoveClosedCaseInternal(product, folderPath, closedRoot, useYearFolder: true, refreshView: false, out _))
                {
                    moved++;
                    if (_activeProduct != null && string.Equals(_activeProduct.Name, product.Name, StringComparison.Ordinal))
                    {
                        refreshNeeded = true;
                    }
                }
                else
                {
                    failed++;
                }
            }
        }

        if (refreshNeeded)
        {
            RefreshView();
        }

        RefreshClosedTab();
        MessageBox.Show(this, $"整理完了: 移動 {moved} / スキップ {skipped} / 失敗 {failed}", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void RefreshStatusTab()
    {
        var requestVersion = Interlocked.Increment(ref _statusRefreshVersion);
        _statusRefreshCts?.Cancel();
        _statusRefreshCts?.Dispose();
        _statusRefreshCts = new CancellationTokenSource();
        var token = _statusRefreshCts.Token;
        var loadingVersion = StartLoading("ステータスタブを読み込み中...");
        IProgress<LoadProgress> progress = new Progress<LoadProgress>(item => ReportLoading(loadingVersion, item.Percent, item.Message));

        _isRefreshingStatusTab = true;

        try
        {
            var snapshot = await Task.Run(() => BuildStatusTabSnapshot(token, progress), token);
            if (token.IsCancellationRequested || requestVersion != _statusRefreshVersion)
            {
                return;
            }

            ApplyStatusTabSnapshot(snapshot);
            _statusTabDirty = false;
            _statusTabRefreshedAtUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to refresh status tab", ex);
            _viewModel.StatusMessage = $"ステータス更新に失敗しました: {ex.Message}";
        }
        finally
        {
            if (requestVersion == _statusRefreshVersion)
            {
                _isRefreshingStatusTab = false;
            }

            StopLoading(loadingVersion);
        }
    }

    private StatusTabSnapshot BuildStatusTabSnapshot(CancellationToken token, IProgress<LoadProgress>? progress)
    {
        progress?.Report(new LoadProgress(5, "ステータス集計を開始しています..."));
        var snapshot = new StatusTabSnapshot();
        snapshot.Products = _settings.Products
            .Select(product => new ProductProfile
            {
                Name = product.Name,
                BasePath = product.BasePath,
                ClosedPath = product.ClosedPath,
                NoteTemplates = product.NoteTemplates ?? new List<Dictionary<string, string>>(),
            })
            .ToList();
        snapshot.ExcludedPaths = _settings.ExcludedCases.Select(NormalizePath).ToList();
        snapshot.CurrentStatuses = _settings.Statuses.ToList();

        if (snapshot.Products.Count == 0)
        {
            snapshot.StatusMessage = "プロダクトが未設定です。";
            progress?.Report(new LoadProgress(100, snapshot.StatusMessage));
            return snapshot;
        }

        var excludedSet = new HashSet<string>(snapshot.ExcludedPaths, StringComparer.OrdinalIgnoreCase);
        var statusList = new List<string>();
        var statusSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var status in snapshot.CurrentStatuses)
        {
            AddStatusOption(statusList, statusSet, status);
        }

        var allCasesByPath = new Dictionary<string, (string ProductName, CaseRecord Record)>(StringComparer.OrdinalIgnoreCase);
        var statusColumns = new List<string>();
        var statusColumnSet = new HashSet<string>(StringComparer.Ordinal);
        var totalStatusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalDirect = 0;
        var totalOpen = 0;
        var totalStale = 0;
        var cutoff = DateTime.Today.AddDays(-StaleDaysThreshold);

        var totalProducts = snapshot.Products.Count;
        var productIndex = 0;
        foreach (var product in snapshot.Products)
        {
            token.ThrowIfCancellationRequested();
            productIndex++;
            var basePercent = 10 + (productIndex - 1) * 75.0 / Math.Max(1, totalProducts);
            progress?.Report(new LoadProgress(basePercent, $"ステータス集計中: {product.Name}"));
            if (string.IsNullOrWhiteSpace(product.BasePath))
            {
                continue;
            }

            var productRoot = NormalizePath(product.BasePath);
            if (string.IsNullOrWhiteSpace(productRoot) || !Directory.Exists(productRoot))
            {
                continue;
            }

            var cases = ScanCasesUnderCached(productRoot, token)
                .Where(record => IsPathUnderBase(productRoot, NormalizePath(record.FolderPath)))
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
            var productStatusCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            totalDirect += directCases.Count;
            totalOpen += openCases.Count;
            totalStale += staleCases.Count;

            snapshot.ProductSummaries.Add(new ProductSummaryEntry
            {
                Name = product.Name,
                Total = directCases.Count,
                Open = openCases.Count,
                Stale = staleCases.Count,
                LatestUpdated = BuildLatestUpdated(directCases),
                StatusCounts = productStatusCounts,
            });

            foreach (var record in openCases)
            {
                var displayStatus = NormalizeStatusLabel(record.Status);
                AddStatusOption(statusList, statusSet, displayStatus);
                AddStatusOption(statusColumns, statusColumnSet, displayStatus);
                productStatusCounts[displayStatus] = productStatusCounts.TryGetValue(displayStatus, out var count) ? count + 1 : 1;
                totalStatusCounts[displayStatus] = totalStatusCounts.TryGetValue(displayStatus, out var totalCount) ? totalCount + 1 : 1;

                snapshot.OpenCases.Add(new OpenCaseEntry
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

                snapshot.StaleCases.Add(new StaleCaseEntry
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
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(excluded))
            {
                continue;
            }

            if (allCasesByPath.TryGetValue(excluded, out var info))
            {
                var displayStatus = NormalizeStatusLabel(info.Record.Status);
                AddStatusOption(statusList, statusSet, displayStatus);

                snapshot.ExcludedCases.Add(new ExcludedCaseEntry
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
                snapshot.ExcludedCases.Add(new ExcludedCaseEntry
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

        var normalizedCurrentStatuses = new List<string>();
        var currentSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var status in snapshot.CurrentStatuses)
        {
            AddStatusOption(normalizedCurrentStatuses, currentSet, status);
        }

        snapshot.ShouldUpdateStatuses = !statusList.SequenceEqual(normalizedCurrentStatuses);
        snapshot.NextStatuses = statusList;

        var orderedStatusColumns = new List<string>();
        var orderedStatusSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var status in statusList)
        {
            if (totalStatusCounts.ContainsKey(status))
            {
                AddStatusOption(orderedStatusColumns, orderedStatusSet, status);
            }
        }

        foreach (var status in statusColumns)
        {
            AddStatusOption(orderedStatusColumns, orderedStatusSet, status);
        }

        foreach (var summary in snapshot.ProductSummaries)
        {
            foreach (var status in orderedStatusColumns)
            {
                if (!summary.StatusCounts.ContainsKey(status))
                {
                    summary.StatusCounts[status] = 0;
                }
            }
        }

        var totalRow = new ProductSummaryEntry
        {
            Name = "合計",
            Total = totalDirect,
            Open = totalOpen,
            Stale = totalStale,
            LatestUpdated = "-",
            StatusCounts = new Dictionary<string, int>(StringComparer.Ordinal),
            IsTotal = true,
        };
        foreach (var status in orderedStatusColumns)
        {
            totalRow.StatusCounts[status] = totalStatusCounts.TryGetValue(status, out var count) ? count : 0;
        }

        snapshot.ProductSummaries.Add(totalRow);
        snapshot.StatusColumns = orderedStatusColumns;
        snapshot.StatusMessage = "ステータスを更新しました。";
        progress?.Report(new LoadProgress(100, snapshot.StatusMessage));
        return snapshot;
    }

    private void ApplyStatusTabSnapshot(StatusTabSnapshot snapshot)
    {
        _productSummaries.Clear();
        _openCases.Clear();
        _staleCases.Clear();
        _excludedCases.Clear();
        RefreshProductNameOptions();

        foreach (var item in snapshot.ProductSummaries)
        {
            _productSummaries.Add(item);
        }

        foreach (var item in snapshot.OpenCases)
        {
            _openCases.Add(item);
        }

        foreach (var item in snapshot.StaleCases)
        {
            _staleCases.Add(item);
        }

        foreach (var item in snapshot.ExcludedCases)
        {
            _excludedCases.Add(item);
        }

        RebuildStatusSummaryColumns(snapshot.StatusColumns);

        if (snapshot.ShouldUpdateStatuses)
        {
            _settings.Statuses = snapshot.NextStatuses;
            _config.Save(_settings);
            RefreshStatusOptions();
        }

        _viewModel.StatusMessage = snapshot.StatusMessage;
    }

    private void RefreshProductNameOptions()
    {
        _productNameOptions.Clear();
        foreach (var name in _settings.Products
                     .Select(product => product.Name?.Trim())
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.Ordinal))
        {
            _productNameOptions.Add(name!);
        }
    }

    private void RebuildStatusSummaryColumns(IReadOnlyList<string> statusColumns)
    {
        if (StatusSummaryGrid == null)
        {
            return;
        }

        StatusSummaryGrid.Columns.Clear();
        StatusSummaryGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "プロダクト名",
            Binding = new System.Windows.Data.Binding(nameof(ProductSummaryEntry.Name)),
            Width = 200,
        });
        StatusSummaryGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "件数",
            Binding = new System.Windows.Data.Binding(nameof(ProductSummaryEntry.Total)),
            Width = 80,
        });
        StatusSummaryGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "進行中",
            Binding = new System.Windows.Data.Binding(nameof(ProductSummaryEntry.Open)),
            Width = 80,
        });
        StatusSummaryGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "停滞(7日+)",
            Binding = new System.Windows.Data.Binding(nameof(ProductSummaryEntry.Stale)),
            Width = 100,
        });
        StatusSummaryGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "最終更新",
            Binding = new System.Windows.Data.Binding(nameof(ProductSummaryEntry.LatestUpdated)),
            Width = 140,
        });

        foreach (var status in statusColumns)
        {
            StatusSummaryGrid.Columns.Add(new DataGridTextColumn
            {
                Header = status,
                Binding = new System.Windows.Data.Binding($"StatusCounts[{status}]")
                {
                    FallbackValue = 0,
                    TargetNullValue = 0,
                },
                Width = 90,
            });
        }
    }

    private void RebuildClosedSummaryColumns(IReadOnlyList<string> years)
    {
        if (ClosedSummaryGrid == null)
        {
            return;
        }

        ClosedSummaryGrid.Columns.Clear();
        ClosedSummaryGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "プロダクト",
            Binding = new System.Windows.Data.Binding(nameof(ClosedSummaryEntry.ProductName)),
            Width = 220,
        });
        ClosedSummaryGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "合計",
            Binding = new System.Windows.Data.Binding(nameof(ClosedSummaryEntry.Total)),
            Width = 90,
        });

        foreach (var year in years)
        {
            ClosedSummaryGrid.Columns.Add(new DataGridTextColumn
            {
                Header = year,
                Binding = new System.Windows.Data.Binding($"YearCounts[{year}]")
                {
                    FallbackValue = 0,
                    TargetNullValue = 0,
                },
                Width = 90,
            });
        }
    }

    private void RebuildClosedMonthlySummaryColumns()
    {
        if (ClosedMonthlySummaryGrid == null)
        {
            return;
        }

        ClosedMonthlySummaryGrid.Columns.Clear();
        ClosedMonthlySummaryGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "プロダクト",
            Binding = new System.Windows.Data.Binding(nameof(ClosedMonthlySummaryEntry.ProductName)),
            Width = 220,
        });
        ClosedMonthlySummaryGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "合計",
            Binding = new System.Windows.Data.Binding(nameof(ClosedMonthlySummaryEntry.Total)),
            Width = 90,
        });

        foreach (var month in ClosedMonthLabels)
        {
            ClosedMonthlySummaryGrid.Columns.Add(new DataGridTextColumn
            {
                Header = month,
                Binding = new System.Windows.Data.Binding($"MonthCounts[{month}]")
                {
                    FallbackValue = 0,
                    TargetNullValue = 0,
                },
                Width = 80,
            });
        }
    }

    private async void RefreshClosedTab()
    {
        CancelClosedSearchFilter();
        var requestVersion = Interlocked.Increment(ref _closedRefreshVersion);
        _closedRefreshCts?.Cancel();
        _closedRefreshCts?.Dispose();
        _closedRefreshCts = new CancellationTokenSource();
        var token = _closedRefreshCts.Token;
        var loadingVersion = StartLoading("クローズタブを読み込み中...");
        IProgress<LoadProgress> progress = new Progress<LoadProgress>(item => ReportLoading(loadingVersion, item.Percent, item.Message));

        try
        {
            var snapshot = await Task.Run(() => BuildClosedTabSnapshot(token, progress), token);
            if (token.IsCancellationRequested || requestVersion != _closedRefreshVersion)
            {
                return;
            }

            ApplyClosedTabSnapshot(snapshot);
            _closedTabDirty = false;
            _closedTabRefreshedAtUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to refresh closed tab", ex);
            _viewModel.StatusMessage = $"クローズ一覧更新に失敗しました: {ex.Message}";
        }
        finally
        {
            StopLoading(loadingVersion);
        }
    }

    private ClosedTabSnapshot BuildClosedTabSnapshot(CancellationToken token, IProgress<LoadProgress>? progress)
    {
        progress?.Report(new LoadProgress(5, "クローズ一覧を集計中..."));
        var snapshot = new ClosedTabSnapshot();
        snapshot.Products = _settings.Products
            .Select(product => new ProductProfile
            {
                Name = product.Name,
                BasePath = product.BasePath,
                ClosedPath = product.ClosedPath,
                NoteTemplates = product.NoteTemplates ?? new List<Dictionary<string, string>>(),
            })
            .ToList();

        if (snapshot.Products.Count == 0)
        {
            snapshot.StatusMessage = "プロダクトが未設定です。";
            progress?.Report(new LoadProgress(100, snapshot.StatusMessage));
            return snapshot;
        }

        var totalProducts = snapshot.Products.Count;
        var productIndex = 0;
        foreach (var product in snapshot.Products)
        {
            token.ThrowIfCancellationRequested();
            productIndex++;
            var basePercent = 10 + (productIndex - 1) * 75.0 / Math.Max(1, totalProducts);
            progress?.Report(new LoadProgress(basePercent, $"クローズ集計中: {product.Name}"));
            if (string.IsNullOrWhiteSpace(product.BasePath))
            {
                continue;
            }

            var baseRoot = NormalizePath(product.BasePath);
            if (string.IsNullOrWhiteSpace(baseRoot) || !Directory.Exists(baseRoot))
            {
                continue;
            }

            var closedRoot = NormalizePath(product.ClosedPath);
            var merged = new Dictionary<string, CaseRecord>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in ScanCasesUnderCached(baseRoot, token))
            {
                var pathKey = NormalizePath(record.FolderPath);
                if (!string.IsNullOrWhiteSpace(pathKey) && !merged.ContainsKey(pathKey))
                {
                    merged[pathKey] = record;
                }
            }

            if (!string.IsNullOrWhiteSpace(closedRoot) && Directory.Exists(closedRoot))
            {
                foreach (var record in ScanCasesUnderCached(closedRoot, token))
                {
                    var pathKey = NormalizePath(record.FolderPath);
                    if (!string.IsNullOrWhiteSpace(pathKey) && !merged.ContainsKey(pathKey))
                    {
                        merged[pathKey] = record;
                    }
                }
            }

            var classified = new Dictionary<string, (ClosedCaseEntry Entry, int LocationRank, string LastUpdated)>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in merged.Values)
            {
                if (!IsClosedStatus(record.Status))
                {
                    continue;
                }

                var normalizedPath = NormalizePath(record.FolderPath);
                var entry = new ClosedCaseEntry
                {
                    ProductName = product.Name,
                    Company = record.Company,
                    SupportNumber = record.SupportNumber,
                    Status = NormalizeStatusLabel(record.Status),
                    CreatedOnRaw = record.CreatedOn,
                    CreatedOnDisplay = FormatCreatedOn(record.CreatedOn),
                    LastUpdatedDisplay = FormatDate(record.LastUpdated),
                    FolderPath = normalizedPath,
                };
                var isInClosedFolder = !string.IsNullOrWhiteSpace(closedRoot) && IsPathUnderBase(closedRoot, normalizedPath);
                var isInBaseDirect = IsDirectChild(baseRoot, normalizedPath);
                // 旧運用で「■2026クローズ」など別名のクローズ配下が残っているケースを救済。
                var isInLegacyClosedFolder = !isInClosedFolder && IsUnderLegacyClosedFolder(baseRoot, normalizedPath);
                if (!isInClosedFolder && !isInLegacyClosedFolder && !isInBaseDirect)
                {
                    continue;
                }

                var key = !string.IsNullOrWhiteSpace(record.NormalizedSupport)
                    ? $"support:{record.NormalizedSupport}"
                    : $"path:{normalizedPath}";
                var rank = isInClosedFolder ? 3 : (isInLegacyClosedFolder ? 2 : 1);
                if (classified.TryGetValue(key, out var existing))
                {
                    // 同一サポート番号がベース側とクローズ側に重複する場合は、
                    // クローズフォルダ側を優先し、同一種別なら更新日時が新しい方を採用。
                    if (rank < existing.LocationRank)
                    {
                        continue;
                    }

                    if (rank == existing.LocationRank &&
                        string.CompareOrdinal(record.LastUpdated ?? string.Empty, existing.LastUpdated) <= 0)
                    {
                        continue;
                    }
                }

                classified[key] = (entry, rank, record.LastUpdated ?? string.Empty);
            }

            foreach (var item in classified.Values)
            {
                if (item.LocationRank >= 2)
                {
                    snapshot.ClosedFolderCasesSource.Add(item.Entry);
                }
                else
                {
                    snapshot.ClosedBaseCasesSource.Add(item.Entry);
                }

                snapshot.ClosedSummaryEntries.Add(item.Entry);
            }
        }

        snapshot.StatusMessage = "クローズ一覧を更新しました。";
        progress?.Report(new LoadProgress(100, snapshot.StatusMessage));
        return snapshot;
    }

    private void ApplyClosedTabSnapshot(ClosedTabSnapshot snapshot)
    {
        ClearClosedSummarySelection();
        ClearClosedMonthlySummarySelection();
        _closedBaseCasesSource.Clear();
        _closedFolderCasesSource.Clear();
        _closedBaseCases.Clear();
        _closedFolderCases.Clear();
        _closedSummaries.Clear();
        _closedMonthlySummaries.Clear();
        _closedSummaryEntries.Clear();

        if (snapshot.Products.Count == 0)
        {
            RebuildClosedSummaryColumns(Array.Empty<string>());
            RefreshClosedMonthlySummary();
            _viewModel.StatusMessage = snapshot.StatusMessage;
            return;
        }

        foreach (var entry in snapshot.ClosedBaseCasesSource)
        {
            _closedBaseCasesSource.Add(entry);
        }

        foreach (var entry in snapshot.ClosedFolderCasesSource)
        {
            _closedFolderCasesSource.Add(entry);
        }

        foreach (var entry in snapshot.ClosedSummaryEntries)
        {
            _closedSummaryEntries.Add(entry);
        }

        RefreshClosedSummary(_closedSummaryEntries);
        RefreshClosedMonthlySummary();
        ApplyClosedSearchFilter();
        _viewModel.StatusMessage = snapshot.StatusMessage;
    }

    private void RefreshClosedSummary(IEnumerable<ClosedCaseEntry> entries)
    {
        _closedSummaries.Clear();

        var uniqueEntries = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FolderPath))
            .GroupBy(entry => NormalizePath(entry.FolderPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var rowsByProduct = new Dictionary<string, ClosedSummaryEntry>(StringComparer.Ordinal);
        foreach (var product in _settings.Products)
        {
            var name = (product.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name) || rowsByProduct.ContainsKey(name))
            {
                continue;
            }

            var row = new ClosedSummaryEntry
            {
                ProductName = name,
            };
            rowsByProduct[name] = row;
            _closedSummaries.Add(row);
        }

        var years = new List<string>();
        var yearsSet = new HashSet<string>(StringComparer.Ordinal);
        var totalsByYear = new Dictionary<string, int>(StringComparer.Ordinal);
        var grandTotal = 0;

        foreach (var entry in uniqueEntries)
        {
            var productName = (entry.ProductName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(productName))
            {
                productName = "-";
            }

            if (!rowsByProduct.TryGetValue(productName, out var row))
            {
                row = new ClosedSummaryEntry
                {
                    ProductName = productName,
                };
                rowsByProduct[productName] = row;
                _closedSummaries.Add(row);
            }

            var year = ResolveClosedSummaryYear(entry);
            AddClosedSummaryYear(years, yearsSet, year);

            row.Total += 1;
            row.YearCounts[year] = row.YearCounts.TryGetValue(year, out var rowYearCount) ? rowYearCount + 1 : 1;
            totalsByYear[year] = totalsByYear.TryGetValue(year, out var totalYearCount) ? totalYearCount + 1 : 1;
            grandTotal += 1;
        }

        foreach (var row in _closedSummaries)
        {
            foreach (var year in years)
            {
                if (!row.YearCounts.ContainsKey(year))
                {
                    row.YearCounts[year] = 0;
                }
            }
        }

        var totalRow = new ClosedSummaryEntry
        {
            ProductName = "全合計",
            Total = grandTotal,
            IsGrandTotal = true,
            YearCounts = new Dictionary<string, int>(StringComparer.Ordinal),
        };
        foreach (var year in years)
        {
            totalRow.YearCounts[year] = totalsByYear.TryGetValue(year, out var count) ? count : 0;
        }

        _closedSummaries.Add(totalRow);
        RebuildClosedSummaryColumns(years);
    }

    private void RefreshClosedMonthlySummary()
    {
        _closedMonthlySummaries.Clear();
        RebuildClosedMonthlySummaryColumns();

        var selectedYear = _closedSummarySelectedYear;
        if (string.IsNullOrWhiteSpace(selectedYear) || !IsYearSegment(selectedYear))
        {
            _closedSummaryFilterMonth = 0;
            ClearClosedMonthlySummarySelection();
            if (ClosedMonthlySummaryGroup != null)
            {
                ClosedMonthlySummaryGroup.Header = "年別月次サマリー (年をクリックして表示)";
            }

            return;
        }

        if (ClosedMonthlySummaryGroup != null)
        {
            ClosedMonthlySummaryGroup.Header = $"年別月次サマリー ({selectedYear}年)";
        }

        var uniqueEntries = _closedSummaryEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FolderPath))
            .GroupBy(entry => NormalizePath(entry.FolderPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(entry => string.Equals(ResolveClosedSummaryYear(entry), selectedYear, StringComparison.Ordinal))
            .ToList();

        var rowsByProduct = new Dictionary<string, ClosedMonthlySummaryEntry>(StringComparer.Ordinal);
        foreach (var product in _settings.Products)
        {
            var name = (product.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name) || rowsByProduct.ContainsKey(name))
            {
                continue;
            }

            var row = CreateClosedMonthlySummaryRow(name);
            rowsByProduct[name] = row;
            _closedMonthlySummaries.Add(row);
        }

        var totals = CreateClosedMonthlySummaryRow("全合計", isGrandTotal: true);

        foreach (var entry in uniqueEntries)
        {
            var productName = (entry.ProductName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(productName))
            {
                productName = "-";
            }

            if (!rowsByProduct.TryGetValue(productName, out var row))
            {
                row = CreateClosedMonthlySummaryRow(productName);
                rowsByProduct[productName] = row;
                _closedMonthlySummaries.Add(row);
            }

            row.Total += 1;
            totals.Total += 1;

            if (TryResolveMonth(entry, out var month))
            {
                var monthLabel = $"{month}月";
                row.MonthCounts[monthLabel] = row.MonthCounts[monthLabel] + 1;
                totals.MonthCounts[monthLabel] = totals.MonthCounts[monthLabel] + 1;
            }
        }

        _closedMonthlySummaries.Add(totals);
    }

    private static ClosedMonthlySummaryEntry CreateClosedMonthlySummaryRow(string productName, bool isGrandTotal = false)
    {
        var row = new ClosedMonthlySummaryEntry
        {
            ProductName = productName,
            IsGrandTotal = isGrandTotal,
            MonthCounts = new Dictionary<string, int>(StringComparer.Ordinal),
        };

        foreach (var month in ClosedMonthLabels)
        {
            row.MonthCounts[month] = 0;
        }

        return row;
    }

    private static string ResolveClosedSummaryYear(ClosedCaseEntry entry)
    {
        if (TryResolveYear(entry.CreatedOnRaw, out var createdOnYear))
        {
            return createdOnYear;
        }

        if (TryResolveYear(entry.CreatedOnDisplay, out var displayYear))
        {
            return displayYear;
        }

        return "年不明";
    }

    private static bool TryResolveYear(string? value, out string yearText)
    {
        yearText = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 4 && int.TryParse(trimmed.Substring(0, 4), out var first4) && first4 >= 1900 && first4 <= 2100)
        {
            yearText = first4.ToString("0000");
            return true;
        }

        if (DateTime.TryParse(trimmed, out var parsed))
        {
            yearText = parsed.Year.ToString("0000");
            return true;
        }

        return false;
    }

    private static bool TryResolveMonth(string? value, out int month)
    {
        month = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 8 &&
            int.TryParse(trimmed.Substring(4, 2), out var monthFromCompact) &&
            monthFromCompact >= 1 && monthFromCompact <= 12)
        {
            month = monthFromCompact;
            return true;
        }

        if (DateTime.TryParse(trimmed, out var parsed))
        {
            month = parsed.Month;
            return true;
        }

        return false;
    }

    private static bool TryResolveMonth(ClosedCaseEntry entry, out int month)
    {
        if (TryResolveMonth(entry.CreatedOnRaw, out month))
        {
            return true;
        }

        return TryResolveMonth(entry.CreatedOnDisplay, out month);
    }

    private static bool TryParseMonthLabel(string value, out int month)
    {
        month = 0;
        if (!value.EndsWith("月", StringComparison.Ordinal))
        {
            return false;
        }

        var rawMonth = value.Substring(0, value.Length - 1);
        if (!int.TryParse(rawMonth, out var parsed))
        {
            return false;
        }

        if (parsed < 1 || parsed > 12)
        {
            return false;
        }

        month = parsed;
        return true;
    }

    private static void AddClosedSummaryYear(List<string> years, HashSet<string> yearsSet, string year)
    {
        if (string.IsNullOrWhiteSpace(year))
        {
            return;
        }

        if (yearsSet.Add(year))
        {
            years.Add(year);
            years.Sort((left, right) =>
            {
                if (left == "年不明")
                {
                    return 1;
                }

                if (right == "年不明")
                {
                    return -1;
                }

                return string.CompareOrdinal(left, right);
            });
        }
    }

    private async void ApplyClosedSearchFilter(bool updateStatusMessage = false)
    {
        var keyword = ClosedSearchTextBox?.Text?.Trim() ?? _closedSearchKeyword;
        _closedSearchKeyword = keyword;
        var productFilter = _closedSummaryFilterProduct;
        var yearFilter = _closedSummaryFilterYear;
        var monthFilter = _closedSummaryFilterMonth;
        var baseSource = _closedBaseCasesSource.ToList();
        var folderSource = _closedFolderCasesSource.ToList();

        var requestVersion = Interlocked.Increment(ref _closedSearchVersion);
        _closedSearchCts?.Cancel();
        _closedSearchCts?.Dispose();
        _closedSearchCts = new CancellationTokenSource();
        var token = _closedSearchCts.Token;

        var loadingVersion = StartLoading("クローズ検索中...");
        _closedSearchLoadingVersion = loadingVersion;
        var loadingStartedAtUtc = DateTime.UtcNow;
        IProgress<LoadProgress> progress = new Progress<LoadProgress>(item => ReportLoading(loadingVersion, item.Percent, item.Message));

        try
        {
            // 年クリック直後に進捗UIを確実に1フレーム描画してから検索開始する。
            await Dispatcher.Yield(DispatcherPriority.Render);

            var filtered = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();

                var total = Math.Max(1, baseSource.Count + folderSource.Count);
                var processed = 0;
                var nextReport = 0;
                progress.Report(new LoadProgress(2, "クローズ検索中..."));

                var filteredBase = new List<ClosedCaseEntry>(baseSource.Count);
                foreach (var entry in baseSource)
                {
                    token.ThrowIfCancellationRequested();
                    if (MatchesClosedSummaryFilter(entry, productFilter, yearFilter, monthFilter) &&
                        MatchesClosedSearch(entry, keyword))
                    {
                        filteredBase.Add(entry);
                    }

                    processed++;
                    var percent = processed * 100 / total;
                    if (percent >= nextReport)
                    {
                        progress.Report(new LoadProgress(percent, $"クローズ検索中... {percent}%"));
                        nextReport += 10;
                    }
                }

                var filteredFolder = new List<ClosedCaseEntry>(folderSource.Count);
                foreach (var entry in folderSource)
                {
                    token.ThrowIfCancellationRequested();
                    if (MatchesClosedSummaryFilter(entry, productFilter, yearFilter, monthFilter) &&
                        MatchesClosedSearch(entry, keyword))
                    {
                        filteredFolder.Add(entry);
                    }

                    processed++;
                    var percent = processed * 100 / total;
                    if (percent >= nextReport)
                    {
                        progress.Report(new LoadProgress(percent, $"クローズ検索中... {percent}%"));
                        nextReport += 10;
                    }
                }

                progress.Report(new LoadProgress(100, "クローズ検索を適用中..."));
                return (filteredBase, filteredFolder);
            }, token);

            if (token.IsCancellationRequested || requestVersion != _closedSearchVersion)
            {
                return;
            }

            if (!IsClosedTabVisible())
            {
                return;
            }

            if (ClosedBaseGrid != null)
            {
                ClosedBaseGrid.ItemsSource = filtered.filteredBase;
            }

            if (ClosedFolderGrid != null)
            {
                ClosedFolderGrid.ItemsSource = filtered.filteredFolder;
            }

            if (updateStatusMessage)
            {
                var total = filtered.filteredBase.Count + filtered.filteredFolder.Count;
                var conditions = new List<string>();
                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    conditions.Add($"検索: {keyword}");
                }

                if (!string.IsNullOrWhiteSpace(productFilter))
                {
                    conditions.Add($"プロダクト: {productFilter}");
                }

                if (!string.IsNullOrWhiteSpace(yearFilter))
                {
                    conditions.Add($"年: {yearFilter}");
                }

                if (monthFilter > 0)
                {
                    conditions.Add($"月: {monthFilter}月");
                }

                _viewModel.StatusMessage = conditions.Count == 0
                    ? "クローズ検索を解除しました。"
                    : $"クローズ絞り込み ({string.Join(" / ", conditions)}): {total}件";
            }
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to apply closed filter", ex);
            _viewModel.StatusMessage = $"クローズ検索に失敗しました: {ex.Message}";
        }
        finally
        {
            if (_closedSearchLoadingVersion == loadingVersion)
            {
                var elapsed = DateTime.UtcNow - loadingStartedAtUtc;
                var remaining = ClosedSearchMinIndicatorDuration - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(remaining);
                    }
                    catch
                    {
                        // no-op
                    }
                }

                _closedSearchLoadingVersion = -1;
            }

            StopLoading(loadingVersion);
        }
    }

    private static bool MatchesClosedSummaryFilter(
        ClosedCaseEntry entry,
        string productFilter,
        string yearFilter,
        int monthFilter)
    {
        if (!string.IsNullOrWhiteSpace(productFilter) &&
            !string.Equals(entry.ProductName, productFilter, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(yearFilter))
        {
            var year = ResolveClosedSummaryYear(entry);
            if (!string.Equals(year, yearFilter, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (monthFilter > 0)
        {
            if (!TryResolveMonth(entry, out var month) || month != monthFilter)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesClosedSearch(ClosedCaseEntry entry, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        return ContainsIgnoreCase(entry.ProductName, keyword)
               || ContainsIgnoreCase(entry.Company, keyword)
               || ContainsIgnoreCase(entry.SupportNumber, keyword)
               || ContainsIgnoreCase(entry.CreatedOnDisplay, keyword)
               || ContainsIgnoreCase(entry.Status, keyword)
               || ContainsIgnoreCase(entry.LastUpdatedDisplay, keyword)
               || ContainsIgnoreCase(entry.FolderPath, keyword);
    }

    private static bool ContainsIgnoreCase(string? source, string keyword)
    {
        return !string.IsNullOrWhiteSpace(source) && source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClosedStatus(string? status)
    {
        return string.Equals(status?.Trim(), "クローズ", StringComparison.Ordinal);
    }

    private static string ResolveTargetRoot(string baseRoot, string closedRoot, string newStatus)
    {
        if (IsClosedStatus(newStatus) && !string.IsNullOrWhiteSpace(closedRoot))
        {
            return closedRoot;
        }

        return baseRoot;
    }

    private static string ResolveCategoryFromFolder(string baseRoot, string closedRoot, string folderPath, string fallbackCategory)
    {
        var normalizedBase = NormalizePath(baseRoot);
        var normalizedClosed = NormalizePath(closedRoot);
        var normalizedFolder = NormalizePath(folderPath);

        var sourceRoot = normalizedBase;
        if (!string.IsNullOrWhiteSpace(normalizedClosed) && IsPathUnderBase(normalizedClosed, normalizedFolder))
        {
            sourceRoot = normalizedClosed;
        }
        else if (!string.IsNullOrWhiteSpace(normalizedBase) && IsPathUnderBase(normalizedBase, normalizedFolder))
        {
            sourceRoot = normalizedBase;
        }

        var category = string.Equals(sourceRoot, normalizedClosed, StringComparison.OrdinalIgnoreCase)
            ? GetCategoryFromClosedPath(normalizedClosed, normalizedFolder)
            : GetCategoryFromPath(sourceRoot, normalizedFolder);
        return string.IsNullOrWhiteSpace(category) ? fallbackCategory : category;
    }

    private static string GetCategoryFromClosedPath(string closedRoot, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(closedRoot))
        {
            return string.Empty;
        }

        try
        {
            var relative = Path.GetRelativePath(closedRoot, folderPath);
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            var segments = relative
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToArray();

            if (segments.Length <= 1)
            {
                return string.Empty;
            }

            var startIndex = IsYearSegment(segments[0]) ? 1 : 0;
            if (segments.Length - startIndex <= 1)
            {
                return string.Empty;
            }

            return segments[startIndex];
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsYearSegment(string value)
    {
        if (value.Length != 4 || !int.TryParse(value, out var year))
        {
            return false;
        }

        return year >= 1900 && year <= 2100;
    }

    private static bool IsUnderYearFolder(string root, string targetPath)
    {
        return !string.IsNullOrWhiteSpace(GetYearFolderFromPath(root, targetPath));
    }

    private static string? GetYearFolderFromPath(string root, string targetPath)
    {
        try
        {
            var relative = Path.GetRelativePath(root, targetPath);
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            {
                return null;
            }

            var segments = relative
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToArray();

            if (segments.Length < 2)
            {
                return null;
            }

            return IsYearSegment(segments[0]) ? segments[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetYearFromCreatedOn(string? createdOn)
    {
        if (!string.IsNullOrWhiteSpace(createdOn))
        {
            var trimmed = createdOn.Trim();
            if (trimmed.Length >= 4 && int.TryParse(trimmed.Substring(0, 4), out var year) && year >= 1900 && year <= 2100)
            {
                return year.ToString("0000");
            }

            if (DateTime.TryParse(trimmed, out var parsed))
            {
                return parsed.Year.ToString("0000");
            }
        }

        return DateTime.Today.Year.ToString("0000");
    }

    private static string BuildClosedTargetDir(string closedRoot, string createdOn, string category)
    {
        var year = GetYearFromCreatedOn(createdOn);
        var target = Path.Combine(closedRoot, year);
        return string.IsNullOrWhiteSpace(category) ? target : Path.Combine(target, category);
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

    private static string FormatCreatedOn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 8 && DateTime.TryParseExact(trimmed, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed.ToString("yyyy/MM/dd");
        }

        if (DateTime.TryParse(trimmed, out var fallback))
        {
            return fallback.ToString("yyyy/MM/dd");
        }

        return trimmed;
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

    private List<CaseRecord> ScanCasesUnderCached(string root, CancellationToken token)
    {
        var normalizedRoot = NormalizePath(root);
        if (string.IsNullOrWhiteSpace(normalizedRoot) || !Directory.Exists(normalizedRoot))
        {
            return new List<CaseRecord>();
        }

        if (!_directoryScanCache.TryGetValue(normalizedRoot, out var rootCache))
        {
            rootCache = new DirectoryScanCacheRoot();
            _directoryScanCache[normalizedRoot] = rootCache;
        }

        var nextNodes = new Dictionary<string, DirectoryScanCacheNode>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(normalizedRoot);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = stack.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateDirectories(current))
                {
                    token.ThrowIfCancellationRequested();
                    var normalizedEntry = NormalizePath(entry);
                    if (string.IsNullOrWhiteSpace(normalizedEntry))
                    {
                        continue;
                    }

                    var ticks = Directory.GetLastWriteTimeUtc(normalizedEntry).Ticks;
                    if (rootCache.Nodes.TryGetValue(normalizedEntry, out var cached) &&
                        cached.LastWriteTimeUtcTicks == ticks)
                    {
                        nextNodes[normalizedEntry] = cached;
                    }
                    else
                    {
                        var info = new DirectoryInfo(normalizedEntry);
                        var parsed = CaseParser.ParseCaseFromDirectory(info);
                        nextNodes[normalizedEntry] = new DirectoryScanCacheNode
                        {
                            Path = normalizedEntry,
                            LastWriteTimeUtcTicks = ticks,
                            Record = parsed,
                        };
                    }

                    stack.Push(normalizedEntry);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Directory scan skipped for {current}: {ex.Message}");
            }
        }

        rootCache.Nodes = nextNodes;
        rootCache.LastScannedUtcTicks = DateTime.UtcNow.Ticks;
        _directoryScanCacheDirty = true;

        return nextNodes.Values
            .Where(node => node.Record != null)
            .Select(node => node.Record!.CloneWith(isFromFolder: true))
            .ToList();
    }

    private void LoadDirectoryScanCache()
    {
        try
        {
            if (!File.Exists(_directoryScanCachePath))
            {
                return;
            }

            var json = File.ReadAllText(_directoryScanCachePath, EncodingPolicy.Utf8NoBom);
            var dto = JsonSerializer.Deserialize<DirectoryScanCacheFileDto>(json);
            if (dto?.Roots == null)
            {
                return;
            }

            _directoryScanCache.Clear();
            foreach (var rootDto in dto.Roots)
            {
                var normalizedRoot = NormalizePath(rootDto.RootPath);
                if (string.IsNullOrWhiteSpace(normalizedRoot))
                {
                    continue;
                }

                var root = new DirectoryScanCacheRoot
                {
                    LastScannedUtcTicks = rootDto.LastScannedUtcTicks,
                };

                foreach (var nodeDto in rootDto.Nodes)
                {
                    var normalizedPath = NormalizePath(nodeDto.Path);
                    if (string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        continue;
                    }

                    root.Nodes[normalizedPath] = new DirectoryScanCacheNode
                    {
                        Path = normalizedPath,
                        LastWriteTimeUtcTicks = nodeDto.LastWriteTimeUtcTicks,
                        Record = nodeDto.Record?.ToRecord(),
                    };
                }

                _directoryScanCache[normalizedRoot] = root;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"scan-cache の読み込みに失敗しました: {ex.Message}");
            _directoryScanCache.Clear();
        }
        finally
        {
            _directoryScanCacheDirty = false;
        }
    }

    private void SaveDirectoryScanCache()
    {
        if (!_directoryScanCacheDirty)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_directoryScanCachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new DirectoryScanCacheFileDto
            {
                Roots = _directoryScanCache.Select(pair => new DirectoryScanCacheRootDto
                {
                    RootPath = pair.Key,
                    LastScannedUtcTicks = pair.Value.LastScannedUtcTicks,
                    Nodes = pair.Value.Nodes.Values.Select(node => new DirectoryScanCacheNodeDto
                    {
                        Path = node.Path,
                        LastWriteTimeUtcTicks = node.LastWriteTimeUtcTicks,
                        Record = node.Record?.ToDto(),
                    }).ToList(),
                }).ToList(),
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            var json = JsonSerializer.Serialize(payload, options);
            File.WriteAllText(_directoryScanCachePath, json, EncodingPolicy.Utf8NoBom);
            _directoryScanCacheDirty = false;
        }
        catch (Exception ex)
        {
            _logger.Warning($"scan-cache の保存に失敗しました: {ex.Message}");
        }
    }

    private static List<CaseRecord> ScanCasesUnder(string root)
    {
        var normalized = NormalizePath(root);
        if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
        {
            return new List<CaseRecord>();
        }

        var stack = new Stack<string>();
        stack.Push(normalized);
        var found = new List<CaseRecord>();

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateDirectories(current))
                {
                    var info = new DirectoryInfo(entry);
                    var record = CaseParser.ParseCaseFromDirectory(info);
                    if (record != null)
                    {
                        found.Add(record);
                    }

                    stack.Push(entry);
                }
            }
            catch
            {
                // ignore inaccessible folders
            }
        }

        return found;
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

    private void OnStaleCaseProductChanged(object sender, SelectionChangedEventArgs e)
    {
        HandleCaseProductChanged(sender, e);
    }

    private void OnOpenCaseProductChanged(object sender, SelectionChangedEventArgs e)
    {
        HandleCaseProductChanged(sender, e);
    }

    private void HandleCaseProductChanged(object sender, SelectionChangedEventArgs e, StaleCaseEntry? staleEntry = null, OpenCaseEntry? openEntry = null)
    {
        if (_isRefreshingStatusTab)
        {
            return;
        }

        if (sender is not System.Windows.Controls.ComboBox combo)
        {
            return;
        }

        // 初期描画時の選択イベントは RemovedItems が空になりやすいため無視する。
        if (e.RemovedItems.Count == 0)
        {
            return;
        }

        staleEntry ??= combo.DataContext as StaleCaseEntry;
        openEntry ??= combo.DataContext as OpenCaseEntry;
        if (staleEntry == null && openEntry == null)
        {
            return;
        }

        var folderPath = staleEntry?.FolderPath ?? openEntry!.FolderPath;
        var newProduct = e.AddedItems
            .OfType<string>()
            .Select(item => item.Trim())
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        if (string.IsNullOrWhiteSpace(newProduct))
        {
            newProduct = combo.SelectedItem?.ToString()?.Trim();
        }
        if (string.IsNullOrWhiteSpace(newProduct))
        {
            return;
        }

        var oldProduct = e.RemovedItems
            .OfType<string>()
            .Select(item => item.Trim())
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        if (string.IsNullOrWhiteSpace(oldProduct))
        {
            oldProduct = ResolveProductNameFromFolder(folderPath);
        }

        if (string.Equals(oldProduct, newProduct, StringComparison.Ordinal))
        {
            return;
        }

        if (TryReassignCaseProduct(oldProduct, newProduct, folderPath))
        {
            QueueStatusTabRefresh();
            return;
        }

        // 失敗時は一覧を再読み込みして UI 表示を元に戻す。
        QueueStatusTabRefresh();
    }

    private string ResolveProductNameFromFolder(string folderPath)
    {
        var normalized = NormalizePath(folderPath);
        foreach (var product in _settings.Products)
        {
            var baseRoot = NormalizePath(product.BasePath);
            if (IsPathUnderBase(baseRoot, normalized))
            {
                return product.Name;
            }
        }

        return string.Empty;
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
            QueueStatusTabRefresh();
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
            QueueStatusTabRefresh();
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

        var baseRoot = NormalizePath(product.BasePath);
        var closedRoot = NormalizePath(product.ClosedPath);
        var category = ResolveCategoryFromFolder(baseRoot, closedRoot, folderPath, record.Category);
        var targetRoot = ResolveTargetRoot(baseRoot, closedRoot, newStatus);
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            MessageBox.Show(this, "移動先のベースフォルダが未設定です。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var targetDir = string.Equals(targetRoot, closedRoot, StringComparison.OrdinalIgnoreCase)
            ? BuildClosedTargetDir(targetRoot, record.CreatedOn, category)
            : (string.IsNullOrWhiteSpace(category) ? targetRoot : Path.Combine(targetRoot, category));
        Directory.CreateDirectory(targetDir);

        var newName = CaseNaming.BuildFolderName(
            record.CreatedOn,
            record.Company,
            record.SupportNumber,
            newStatus,
            DateTime.Now.ToString("yyyyMMdd"));
        var newPath = Path.Combine(targetDir, newName);

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
                category,
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
            MarkStatusAndClosedTabsDirty();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ステータス更新に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool TryReassignCaseProduct(string sourceProductName, string targetProductName, string folderPath)
    {
        var sourceProduct = _settings.Products.FirstOrDefault(p => string.Equals(p.Name, sourceProductName, StringComparison.Ordinal));
        if (sourceProduct == null)
        {
            MessageBox.Show(this, $"移動元プロダクト設定が見つかりません: {sourceProductName}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var targetProduct = _settings.Products.FirstOrDefault(p => string.Equals(p.Name, targetProductName, StringComparison.Ordinal));
        if (targetProduct == null)
        {
            MessageBox.Show(this, $"移動先プロダクト設定が見つかりません: {targetProductName}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
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

        var sourceBase = NormalizePath(sourceProduct.BasePath);
        var sourceClosed = NormalizePath(sourceProduct.ClosedPath);
        var targetBase = NormalizePath(targetProduct.BasePath);
        var targetClosed = NormalizePath(targetProduct.ClosedPath);
        if (string.IsNullOrWhiteSpace(targetBase))
        {
            MessageBox.Show(this, "移動先プロダクトのベースフォルダが未設定です。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var newStatus = NormalizeStatusLabel(record.Status);
        // プロダクト再割当は「移動先プロダクト配下へ移す」要件のため、
        // 未クローズ案件は常に移動先ベース直下へ配置する。
        var targetRoot = IsClosedStatus(newStatus) && !string.IsNullOrWhiteSpace(targetClosed)
            ? targetClosed
            : targetBase;
        var targetDir = string.Equals(targetRoot, targetClosed, StringComparison.OrdinalIgnoreCase)
            ? BuildClosedTargetDir(targetRoot, record.CreatedOn, string.Empty)
            : targetRoot;
        Directory.CreateDirectory(targetDir);

        var newName = CaseNaming.BuildFolderName(
            record.CreatedOn,
            record.Company,
            record.SupportNumber,
            newStatus,
            DateTime.Now.ToString("yyyyMMdd"));
        var newPath = Path.Combine(targetDir, newName);
        if (Directory.Exists(newPath))
        {
            MessageBox.Show(this, "移動先に同名のフォルダが既に存在します。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
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
                string.Empty,
                false);

            _settings.RecentCases = _settings.RecentCases
                .Where(item => !string.Equals(item, folderPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _config.Save(_settings);
            _config.AddRecentCase(_settings, newPath);

            _caseCache.Remove(folderPath);
            _caseCache[newPath] = updated;

            var sourceRepo = new CaseRepository(_logger);
            sourceRepo.SetBasePath(sourceProduct.BasePath);
            sourceRepo.AllCases();

            if (!string.Equals(sourceProduct.Name, targetProduct.Name, StringComparison.Ordinal))
            {
                var targetRepo = new CaseRepository(_logger);
                targetRepo.SetBasePath(targetProduct.BasePath);
                targetRepo.AllCases();
            }

            if (_activeProduct != null &&
                (string.Equals(_activeProduct.Name, sourceProduct.Name, StringComparison.Ordinal) ||
                 string.Equals(_activeProduct.Name, targetProduct.Name, StringComparison.Ordinal)))
            {
                RefreshView();
            }

            _viewModel.StatusMessage = $"プロダクトを変更しました: {sourceProduct.Name} -> {targetProduct.Name} ({newPath})";
            MarkStatusAndClosedTabsDirty();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"プロダクト変更に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void OnClosedBaseMove(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is ClosedCaseEntry entry)
        {
            MoveClosedCaseToClosedFolder(entry);
        }
    }

    private void OnClosedFolderRestore(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is ClosedCaseEntry entry)
        {
            RestoreClosedCaseToBase(entry);
        }
    }

    private void OnClosedCaseOpen(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is ClosedCaseEntry entry)
        {
            NavigateToClosedCase(entry);
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

    private void NavigateToClosedCase(ClosedCaseEntry entry)
    {
        var targetTab = MainTabControl.Items
            .OfType<TabItem>()
            .FirstOrDefault(item => item.Tag is ProductProfile profile &&
                                    string.Equals(profile.Name, entry.ProductName, StringComparison.Ordinal));

        if (targetTab == null)
        {
            MessageBox.Show(this, $"対象プロダクトが見つかりません: {entry.ProductName}", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.FolderPath) || !Directory.Exists(entry.FolderPath))
        {
            MessageBox.Show(this, "案件フォルダが見つかりません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MainTabControl.SelectedItem = targetTab;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var record = CaseParser.ParseCaseFromDirectory(new DirectoryInfo(entry.FolderPath));
            if (record == null)
            {
                MessageBox.Show(this, "案件情報の解析に失敗しました。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetCurrentCase(record);
            NoteEditorTextBox.Clear();
            _isNotePreviewActive = false;
            _notePreviewBody = string.Empty;
            _viewModel.StatusMessage = "クローズ案件を開きました。";
        }), DispatcherPriority.Background);
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

    private void MoveClosedCaseToClosedFolder(ClosedCaseEntry entry)
    {
        var product = _settings.Products.FirstOrDefault(p => string.Equals(p.Name, entry.ProductName, StringComparison.Ordinal));
        if (product == null)
        {
            MessageBox.Show(this, $"プロダクト設定が見つかりません: {entry.ProductName}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(product.ClosedPath))
        {
            MessageBox.Show(this, "クローズフォルダが未設定です。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var baseRoot = NormalizePath(product.BasePath);
        var closedRoot = NormalizePath(product.ClosedPath);
        if (string.IsNullOrWhiteSpace(closedRoot) || string.Equals(baseRoot, closedRoot, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "クローズフォルダが無効です。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MoveClosedCase(entry, baseRoot, closedRoot, closedRoot, useYearFolder: true);
    }

    private void RestoreClosedCaseToBase(ClosedCaseEntry entry)
    {
        var product = _settings.Products.FirstOrDefault(p => string.Equals(p.Name, entry.ProductName, StringComparison.Ordinal));
        if (product == null)
        {
            MessageBox.Show(this, $"プロダクト設定が見つかりません: {entry.ProductName}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var baseRoot = NormalizePath(product.BasePath);
        var closedRoot = NormalizePath(product.ClosedPath);
        if (string.IsNullOrWhiteSpace(baseRoot))
        {
            MessageBox.Show(this, "ベースフォルダが未設定です。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MoveClosedCase(entry, baseRoot, closedRoot, baseRoot, useYearFolder: false);
    }

    private void MoveClosedCase(ClosedCaseEntry entry, string baseRoot, string closedRoot, string targetRoot, bool useYearFolder)
    {
        var folderPath = entry.FolderPath;
        var product = _settings.Products.FirstOrDefault(p => string.Equals(p.Name, entry.ProductName, StringComparison.Ordinal));
        if (product == null)
        {
            MessageBox.Show(this, $"プロダクト設定が見つかりません: {entry.ProductName}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!TryMoveClosedCaseInternal(product, folderPath, targetRoot, useYearFolder, refreshView: true, out var error))
        {
            MessageBox.Show(this, error ?? "フォルダ移動に失敗しました。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        RefreshClosedTab();
    }

    private bool TryMoveClosedCaseInternal(ProductProfile product, string folderPath, string targetRoot, bool useYearFolder, bool refreshView, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            error = "案件フォルダが見つかりません。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            error = "移動先フォルダが未設定です。";
            return false;
        }

        var record = CaseParser.ParseCaseFromDirectory(new DirectoryInfo(folderPath));
        if (record == null)
        {
            error = "案件情報の解析に失敗しました。";
            return false;
        }

        var baseRoot = NormalizePath(product.BasePath);
        var closedRoot = NormalizePath(product.ClosedPath);
        var category = ResolveCategoryFromFolder(baseRoot, closedRoot, folderPath, record.Category);
        var targetDir = useYearFolder
            ? BuildClosedTargetDir(targetRoot, record.CreatedOn, category)
            : (string.IsNullOrWhiteSpace(category) ? targetRoot : Path.Combine(targetRoot, category));
        Directory.CreateDirectory(targetDir);

        var newName = Path.GetFileName(folderPath);
        var newPath = Path.Combine(targetDir, newName);

        if (Directory.Exists(newPath))
        {
            error = "移動先に同名フォルダが既に存在します。";
            return false;
        }

        try
        {
            Directory.Move(folderPath, newPath);

            var updated = new CaseRecord(
                record.Company,
                record.SupportNumber,
                record.Status,
                record.CreatedOn,
                newName,
                newPath,
                CaseNaming.ToIsoTimestamp(DateTime.UtcNow),
                category,
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

            if (refreshView && _activeProduct != null &&
                string.Equals(_activeProduct.Name, product.Name, StringComparison.Ordinal))
            {
                RefreshView();
            }

            MarkStatusAndClosedTabsDirty();
            return true;
        }
        catch (Exception ex)
        {
            error = $"フォルダ移動に失敗しました: {ex.Message}";
            return false;
        }
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
        MarkStatusTabDirty(refreshIfVisible: true);
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
        MarkStatusTabDirty(refreshIfVisible: true);
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

    private static bool IsUnderLegacyClosedFolder(string basePath, string? targetPath)
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

        try
        {
            var relative = Path.GetRelativePath(basePath, target);
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            {
                return false;
            }

            var segments = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                return false;
            }

            for (var i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                if (segment.Contains("クローズ", StringComparison.Ordinal) ||
                    segment.Contains("close", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
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

        OpenCaseFolder(_currentCase.FolderPath);
    }

    private void OpenCaseFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            MessageBox.Show(this, "案件フォルダが見つかりません。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
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

    private void OnCompanyCopyDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CopyToClipboard(CompanyTextBox.Text, "会社名をコピーしました。");
        e.Handled = true;
    }

    private void OnSupportCopyDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CopyToClipboard(SupportTextBox.Text, "サポート番号をコピーしました。");
        e.Handled = true;
    }

    private void OnPreviewBaseCopyDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var baseText = ExtractPreviewBase(PreviewTextBox.Text);
        CopyToClipboard(baseText, "作成フォルダの先頭をコピーしました。");
        e.Handled = true;
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

    private static string ExtractPreviewBase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        var endIndex = trimmed.IndexOf(')');
        if (endIndex >= 0)
        {
            return trimmed[..(endIndex + 1)];
        }

        return trimmed;
    }

    private void CopyToClipboard(string? value, string message)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(trimmed);
            _viewModel.StatusMessage = message;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"コピーに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var baseRoot = NormalizePath(_activeProduct?.BasePath ?? _repository.BasePath ?? string.Empty);
        var closedRoot = NormalizePath(_activeProduct?.ClosedPath ?? string.Empty);
        var fallbackCategory = GetSelectedCategoryName();
        if (string.IsNullOrWhiteSpace(fallbackCategory))
        {
            fallbackCategory = _currentCase.Category;
        }

        var category = ResolveCategoryFromFolder(baseRoot, closedRoot, folder, fallbackCategory);
        var targetRoot = ResolveTargetRoot(baseRoot, closedRoot, newStatus);
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            MessageBox.Show(this, "移動先のベースフォルダが未設定です。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var createdOn = CreatedDatePicker.SelectedDate?.ToString("yyyyMMdd") ?? string.Empty;
        var targetDir = string.Equals(targetRoot, closedRoot, StringComparison.OrdinalIgnoreCase)
            ? BuildClosedTargetDir(targetRoot, createdOn, category)
            : (string.IsNullOrWhiteSpace(category) ? targetRoot : System.IO.Path.Combine(targetRoot, category));
        Directory.CreateDirectory(targetDir);

        var newName = CaseNaming.BuildFolderName(
            createdOn,
            CompanyTextBox.Text,
            SupportTextBox.Text,
            newStatus,
            DateTime.Now.ToString("yyyyMMdd"));
        var newPath = System.IO.Path.Combine(targetDir, newName);

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
                category,
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
            MarkStatusAndClosedTabsDirty();
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
            MarkStatusAndClosedTabsDirty();
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

    private void OnNoteEdit(object sender, RoutedEventArgs e)
    {
        var text = LoadNoteText();
        if (text == null)
        {
            return;
        }

        NoteEditorTextBox.Text = text;
        _isNotePreviewActive = false;
        _notePreviewBody = string.Empty;
        _viewModel.StatusMessage = "ノート内容を読み込みました。";
    }

    private void OnNotePreviewAll(object sender, RoutedEventArgs e)
    {
        var text = LoadNoteText();
        if (text == null)
        {
            return;
        }

        var preview = BuildPreviewAll(text);
        _notePreviewBody = preview;
        _isNotePreviewActive = true;
        NoteEditorTextBox.Text = preview;
        _viewModel.StatusMessage = "ノート全体のプレビューを表示しました。";
    }

    private void OnNotePreviewLatest(object sender, RoutedEventArgs e)
    {
        var text = LoadNoteText();
        if (text == null)
        {
            return;
        }

        var segments = ParseNoteSegments(text);
        if (segments.Count == 0)
        {
            _notePreviewBody = string.Empty;
            _isNotePreviewActive = true;
            NoteEditorTextBox.Text = string.Empty;
            _viewModel.StatusMessage = "プレビューを表示しました。";
            return;
        }

        var latest = PickLatestSegment(segments);
        _notePreviewBody = latest.Body ?? string.Empty;
        _isNotePreviewActive = true;
        NoteEditorTextBox.Text = BuildPreviewFromSegment(latest);
        _viewModel.StatusMessage = "プレビューを表示しました。";
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
        _isNotePreviewActive = false;
        _notePreviewBody = string.Empty;
        _currentCase.LastUpdated = CaseNaming.ToIsoTimestamp(DateTime.UtcNow);
        _repository.UpdateCaseEntry(_currentCase);
        _viewModel.StatusMessage = "ノートを追記しました。";
    }

    private string? LoadNoteText()
    {
        if (_currentCase == null)
        {
            MessageBox.Show(this, "案件を選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var path = GetNoteFilePath();
        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show(this, "ノートファイルが見つかりません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        if (!File.Exists(path))
        {
            NoteService.EnsureNoteFile(_currentCase.FolderPath, _currentNote, _currentCase.SupportNumber);
        }

        try
        {
            var data = File.ReadAllBytes(path);
            return EncodingPolicy.DecodeNoteText(data);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ノートを読み込めません: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    private static string BuildPreviewAll(string text)
    {
        var segments = ParseNoteSegments(text);
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        if (segments.Count == 1 && string.IsNullOrEmpty(segments[0].Header))
        {
            return segments[0].Body;
        }

        var ordered = OrderSegments(segments);
        var lines = new List<string>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var segment = ordered[i];
            if (!string.IsNullOrEmpty(segment.Header))
            {
                lines.Add(segment.Header);
            }

            if (!string.IsNullOrWhiteSpace(segment.Body))
            {
                lines.Add(segment.Body);
            }

            if (i != ordered.Count - 1)
            {
                lines.Add("--------------------------------------------------");
            }
        }

        return string.Join(EncodingPolicy.LineEnding, lines);
    }

    private static string BuildPreviewLatest(string text)
    {
        var segments = ParseNoteSegments(text);
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        var latest = PickLatestSegment(segments);
        return BuildPreviewFromSegment(latest);
    }

    private static string BuildPreviewFromSegment(NoteSegment segment)
    {
        if (string.IsNullOrEmpty(segment.Header))
        {
            return segment.Body;
        }

        if (string.IsNullOrWhiteSpace(segment.Body))
        {
            return segment.Header;
        }

        return string.Join(EncodingPolicy.LineEnding, segment.Header, segment.Body);
    }

    private static List<NoteSegment> ParseNoteSegments(string text)
    {
        var segments = new List<NoteSegment>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return segments;
        }

        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var preHeader = new List<string>();
        var body = new List<string>();
        var currentHeader = string.Empty;
        var currentTimestamp = (DateTime?)null;
        var hasHeader = false;
        var index = 0;

        foreach (var raw in lines)
        {
            var line = raw ?? string.Empty;
            if (line.StartsWith("*****追記部_", StringComparison.Ordinal))
            {
                if (hasHeader)
                {
                    segments.Add(new NoteSegment(currentHeader, JoinLines(body), currentTimestamp, index++));
                    body.Clear();
                }
                else if (preHeader.Count > 0)
                {
                    segments.Add(new NoteSegment(string.Empty, JoinLines(preHeader), null, index++));
                    preHeader.Clear();
                }

                currentHeader = line.TrimEnd();
                currentTimestamp = TryParseNoteHeaderTimestamp(currentHeader, out var parsed) ? parsed : null;
                hasHeader = true;
                continue;
            }

            if (line.Trim() == "--------------------------------------------------")
            {
                continue;
            }

            if (!hasHeader)
            {
                preHeader.Add(line);
            }
            else
            {
                body.Add(line);
            }
        }

        if (hasHeader)
        {
            segments.Add(new NoteSegment(currentHeader, JoinLines(body), currentTimestamp, index++));
        }
        else if (preHeader.Count > 0)
        {
            segments.Add(new NoteSegment(string.Empty, JoinLines(preHeader), null, index++));
        }

        return segments;
    }

    private static List<NoteSegment> OrderSegments(List<NoteSegment> segments)
    {
        var withTimestamp = segments
            .Where(seg => seg.Timestamp.HasValue)
            .OrderByDescending(seg => seg.Timestamp!.Value)
            .ThenByDescending(seg => seg.Index)
            .ToList();

        var withoutTimestamp = segments
            .Where(seg => !seg.Timestamp.HasValue)
            .OrderBy(seg => seg.Index)
            .ToList();

        withTimestamp.AddRange(withoutTimestamp);
        return withTimestamp;
    }

    private static NoteSegment PickLatestSegment(List<NoteSegment> segments)
    {
        var withBody = segments
            .Where(seg => !string.IsNullOrWhiteSpace(seg.Body))
            .ToList();

        if (withBody.Count > 0)
        {
            var withTimestamp = withBody
                .Where(seg => seg.Timestamp.HasValue)
                .OrderByDescending(seg => seg.Timestamp!.Value)
                .ThenByDescending(seg => seg.Index)
                .FirstOrDefault();

            if (withTimestamp.Timestamp.HasValue)
            {
                return withTimestamp;
            }

            return withBody.OrderByDescending(seg => seg.Index).First();
        }

        var fallbackTimestamp = segments
            .Where(seg => seg.Timestamp.HasValue)
            .OrderByDescending(seg => seg.Timestamp!.Value)
            .ThenByDescending(seg => seg.Index)
            .FirstOrDefault();

        if (fallbackTimestamp.Timestamp.HasValue)
        {
            return fallbackTimestamp;
        }

        return segments[^1];
    }

    private static bool TryParseNoteHeaderTimestamp(string header, out DateTime timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var marker = "追記部_";
        var startIndex = header.IndexOf(marker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return false;
        }

        startIndex += marker.Length;
        var endIndex = header.IndexOf('(', startIndex);
        var candidate = endIndex >= 0
            ? header.Substring(startIndex, endIndex - startIndex)
            : header.Substring(startIndex);
        candidate = candidate.Trim();

        var formats = new[] { "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm" };
        if (DateTime.TryParseExact(candidate, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            timestamp = parsed;
            return true;
        }

        if (DateTime.TryParse(candidate, out parsed))
        {
            timestamp = parsed;
            return true;
        }

        return false;
    }

    private static string JoinLines(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join(EncodingPolicy.LineEnding, lines);
    }

    private void OnNoteClear(object sender, RoutedEventArgs e)
    {
        NoteEditorTextBox.Clear();
        _isNotePreviewActive = false;
        _notePreviewBody = string.Empty;
        _viewModel.StatusMessage = "入力内容をクリアしました。";
    }

    private void OnNotePreviewDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_isNotePreviewActive)
        {
            return;
        }

        var body = _notePreviewBody?.Trim();
        if (string.IsNullOrEmpty(body))
        {
            MessageBox.Show(this, "本文がありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new NotePreviewDialog(body)
        {
            Owner = this
        };
        dialog.ShowDialog();
        e.Handled = true;
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

    private async void OnAiAssistantOpen(object sender, RoutedEventArgs e)
    {
        if (_currentCase == null)
        {
            _viewModel.StatusMessage = "AI回答支援を開くには案件を選択してください。";
            MessageBox.Show(this, "案件を選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var context = _aiLaunchContextBuilder.BuildFromCurrentState(BuildAiAssistantCurrentState());
            var contextFilePath = await _aiHandoffFileWriter.WriteAsync(context);
            await _aiProcessLauncher.LaunchAsync(contextFilePath);

            _logger.Info("AI assistant launched with handoff context.");
            _viewModel.StatusMessage = "AI回答支援を起動しました。";
        }
        catch (Exception ex)
        {
            var message = BuildSafeAiAssistantErrorMessage(ex);
            _logger.Error("AI回答支援の起動に失敗しました。", ex);
            _viewModel.StatusMessage = $"AI回答支援の起動に失敗しました: {message}";
            MessageBox.Show(this, $"AI回答支援の起動に失敗しました。\n{message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private AiAssistantCurrentState BuildAiAssistantCurrentState()
    {
        var selectedDate = CreatedDatePicker.SelectedDate;
        return new AiAssistantCurrentState
        {
            ProductName = _activeProduct?.Name ?? _settings.ActiveProduct ?? string.Empty,
            BaseFolder = FirstNonEmpty(_activeProduct?.BasePath, BasePathTextBox.Text, _viewModel.BasePath),
            CloseFolder = _activeProduct?.ClosedPath ?? string.Empty,
            CaseFolderPath = _currentCase?.FolderPath ?? string.Empty,
            CompanyName = FirstNonEmpty(CompanyTextBox.Text, _currentCase?.Company),
            SupportNumber = FirstNonEmpty(SupportTextBox.Text, _currentCase?.SupportNumber),
            Status = FirstNonEmpty(StatusComboBox.Text, _currentCase?.Status),
            ReceptionDate = selectedDate.HasValue ? DateOnly.FromDateTime(selectedDate.Value) : null,
            NoteKind = _currentNote.Label,
            NoteFilePath = GetNoteFilePath(),
            SelectedText = NoteEditorTextBox.SelectedText ?? string.Empty,
            CurrentNoteText = NoteEditorTextBox.Text ?? string.Empty,
        };
    }

    private static string BuildSafeAiAssistantErrorMessage(Exception ex)
    {
        var message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        message = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return message.Length <= 300 ? message : message[..300] + "...";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
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
            foreach (var entry in GetActiveTemplateList())
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

        var existing = GetActiveTemplateList();
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

        SaveActiveTemplates(existing);
        RefreshTemplateCombo(lastImported);
        _viewModel.StatusMessage = "テンプレートをインポートしました。";
        MessageBox.Show(this, $"追加: {added} / 更新: {updated} / スキップ: {skipped}", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnTemplateExport(object sender, RoutedEventArgs e)
    {
        var templates = NormalizeTemplates(GetActiveTemplateList());
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
        var templates = GetActiveTemplateList();
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

        SaveActiveTemplates(templates);
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

        var templates = GetActiveTemplateList();
        if (templates.Any(entry => entry.TryGetValue("name", out var n) && n == newName))
        {
            MessageBox.Show(this, "同名のテンプレートが存在します。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var entry in templates)
        {
            if (entry.TryGetValue("name", out var name) && name == oldName)
            {
                entry["name"] = newName;
                break;
            }
        }

        SaveActiveTemplates(templates);
        RefreshTemplateCombo(newName);
        _viewModel.StatusMessage = "テンプレート名を変更しました。";
    }

    private void DeleteTemplateEntry(string name)
    {
        var templates = GetActiveTemplateList()
            .Where(entry => !(entry.TryGetValue("name", out var n) && n == name))
            .ToList();
        SaveActiveTemplates(templates);
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
        var templates = GetActiveTemplateList();
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
        SaveActiveTemplates(templates);
        RefreshTemplateCombo(name);
        _viewModel.StatusMessage = "テンプレート順を変更しました。";
        if (keepDropDownOpen)
        {
            TemplateComboBox.IsDropDownOpen = true;
        }
        return true;
    }

    private List<Dictionary<string, string>> GetActiveTemplateList()
    {
        if (_activeProduct != null)
        {
            _activeProduct.NoteTemplates ??= new List<Dictionary<string, string>>();
            return _activeProduct.NoteTemplates;
        }

        _settings.NoteTemplates ??= new List<Dictionary<string, string>>();
        return _settings.NoteTemplates;
    }

    private void SaveActiveTemplates(List<Dictionary<string, string>> templates)
    {
        if (_activeProduct != null)
        {
            _activeProduct.NoteTemplates = templates;
        }
        else
        {
            _settings.NoteTemplates = templates;
        }

        _config.Save(_settings);
    }

    private void EnsureActiveProductTemplates()
    {
        if (_activeProduct == null)
        {
            return;
        }

        _activeProduct.NoteTemplates ??= new List<Dictionary<string, string>>();
        if (_activeProduct.NoteTemplates.Count > 0)
        {
            return;
        }

        if (_settings.NoteTemplates.Count == 0)
        {
            return;
        }

        _activeProduct.NoteTemplates = NormalizeTemplates(_settings.NoteTemplates);
        _config.Save(_settings);
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
        public string ClosedPath { get; set; } = string.Empty;
        public List<Dictionary<string, string>> NoteTemplates { get; set; } = new();
    }

    private sealed class ProductSummaryEntry
    {
        public string Name { get; set; } = string.Empty;
        public int Total { get; set; }
        public int Open { get; set; }
        public int Stale { get; set; }
        public string LatestUpdated { get; set; } = "-";
        public Dictionary<string, int> StatusCounts { get; set; } = new(StringComparer.Ordinal);
        public bool IsTotal { get; set; }
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

    private sealed class ClosedCaseEntry
    {
        public string ProductName { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public string SupportNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string CreatedOnRaw { get; set; } = string.Empty;
        public string CreatedOnDisplay { get; set; } = string.Empty;
        public string LastUpdatedDisplay { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
    }

    private sealed class ClosedSummaryEntry
    {
        public string ProductName { get; set; } = string.Empty;
        public int Total { get; set; }
        public Dictionary<string, int> YearCounts { get; set; } = new(StringComparer.Ordinal);
        public bool IsGrandTotal { get; set; }
    }

    private sealed class ClosedMonthlySummaryEntry
    {
        public string ProductName { get; set; } = string.Empty;
        public int Total { get; set; }
        public Dictionary<string, int> MonthCounts { get; set; } = new(StringComparer.Ordinal);
        public bool IsGrandTotal { get; set; }
    }

    private sealed class StatusTabSnapshot
    {
        public List<ProductProfile> Products { get; set; } = new();
        public List<string> ExcludedPaths { get; set; } = new();
        public List<string> CurrentStatuses { get; set; } = new();
        public List<ProductSummaryEntry> ProductSummaries { get; set; } = new();
        public List<OpenCaseEntry> OpenCases { get; set; } = new();
        public List<StaleCaseEntry> StaleCases { get; set; } = new();
        public List<ExcludedCaseEntry> ExcludedCases { get; set; } = new();
        public List<string> StatusColumns { get; set; } = new();
        public bool ShouldUpdateStatuses { get; set; }
        public List<string> NextStatuses { get; set; } = new();
        public string StatusMessage { get; set; } = string.Empty;
    }

    private sealed class ClosedTabSnapshot
    {
        public List<ProductProfile> Products { get; set; } = new();
        public List<ClosedCaseEntry> ClosedBaseCasesSource { get; set; } = new();
        public List<ClosedCaseEntry> ClosedFolderCasesSource { get; set; } = new();
        public List<ClosedCaseEntry> ClosedSummaryEntries { get; set; } = new();
        public string StatusMessage { get; set; } = string.Empty;
    }

    private sealed class DirectoryScanCacheRoot
    {
        public long LastScannedUtcTicks { get; set; }
        public Dictionary<string, DirectoryScanCacheNode> Nodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class DirectoryScanCacheNode
    {
        public string Path { get; set; } = string.Empty;
        public long LastWriteTimeUtcTicks { get; set; }
        public CaseRecord? Record { get; set; }
    }

    private sealed class DirectoryScanCacheFileDto
    {
        public List<DirectoryScanCacheRootDto> Roots { get; set; } = new();
    }

    private sealed class DirectoryScanCacheRootDto
    {
        public string RootPath { get; set; } = string.Empty;
        public long LastScannedUtcTicks { get; set; }
        public List<DirectoryScanCacheNodeDto> Nodes { get; set; } = new();
    }

    private sealed class DirectoryScanCacheNodeDto
    {
        public string Path { get; set; } = string.Empty;
        public long LastWriteTimeUtcTicks { get; set; }
        public CaseRecordDto? Record { get; set; }
    }

    private readonly record struct LoadProgress(double Percent, string Message);

    private readonly record struct NoteSegment(string Header, string Body, DateTime? Timestamp, int Index);

    private readonly record struct TemplateEntry(string Name, string Text);
}
