using System.Collections.ObjectModel;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed class ProductKnowledgeViewModel : ObservableObject
{
    private Guid productId;
    private string productName = string.Empty;
    private string baseFolder = string.Empty;
    private string closeFolder = string.Empty;
    private string productPromptFilePath = string.Empty;
    private bool isEnabled = true;
    private int sortOrder;
    private int crawlMaxDepth = ProductKnowledgeSettings.DefaultCrawlMaxDepth;
    private int crawlMaxPages = ProductKnowledgeSettings.DefaultCrawlMaxPages;

    public ProductKnowledgeViewModel()
    {
        HookCollectionChanges();
    }

    public Guid ProductId
    {
        get => productId;
        set => SetProperty(ref productId, value);
    }

    public string ProductName
    {
        get => productName;
        set => SetProperty(ref productName, value);
    }

    public string BaseFolder
    {
        get => baseFolder;
        set => SetProperty(ref baseFolder, value);
    }

    public string CloseFolder
    {
        get => closeFolder;
        set => SetProperty(ref closeFolder, value);
    }

    public string ProductPromptFilePath
    {
        get => productPromptFilePath;
        set => SetProperty(ref productPromptFilePath, value);
    }

    public ObservableCollection<string> Aliases { get; } = [];

    public ObservableCollection<string> ManualFolders { get; } = [];

    public ObservableCollection<string> DocumentUrls { get; } = [];

    public bool IsEnabled
    {
        get => isEnabled;
        set => SetProperty(ref isEnabled, value);
    }

    public int SortOrder
    {
        get => sortOrder;
        set => SetProperty(ref sortOrder, value);
    }

    public int CrawlMaxDepth
    {
        get => crawlMaxDepth;
        set => SetProperty(ref crawlMaxDepth, value);
    }

    public int CrawlMaxPages
    {
        get => crawlMaxPages;
        set => SetProperty(ref crawlMaxPages, value);
    }

    public string ManualFoldersSummary => ManualFolders.Count == 0
        ? "-"
        : string.Join("; ", ManualFolders);

    public string DocumentUrlsSummary => DocumentUrls.Count == 0
        ? "-"
        : string.Join("; ", DocumentUrls);

    public ProductKnowledgeSettings ToSettings()
    {
        return new ProductKnowledgeSettings
        {
            ProductId = ProductId,
            ProductName = ProductName?.Trim() ?? string.Empty,
            Aliases = Aliases
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            BaseFolder = BaseFolder?.Trim() ?? string.Empty,
            CloseFolder = CloseFolder?.Trim() ?? string.Empty,
            ProductPromptFilePath = ProductPromptFilePath?.Trim() ?? string.Empty,
            ManualFolders = ManualFolders
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DocumentUrls = DocumentUrls
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            IsEnabled = IsEnabled,
            SortOrder = SortOrder,
            CrawlMaxDepth = CrawlMaxDepth,
            CrawlMaxPages = CrawlMaxPages,
        };
    }

    public static ProductKnowledgeViewModel FromSettings(ProductKnowledgeSettings settings)
    {
        var viewModel = new ProductKnowledgeViewModel
        {
            ProductId = settings.ProductId,
            ProductName = settings.ProductName,
            BaseFolder = settings.BaseFolder,
            CloseFolder = settings.CloseFolder,
            ProductPromptFilePath = settings.ProductPromptFilePath,
            IsEnabled = settings.IsEnabled,
            SortOrder = settings.SortOrder,
            CrawlMaxDepth = settings.CrawlMaxDepth,
            CrawlMaxPages = settings.CrawlMaxPages,
        };

        foreach (var alias in settings.Aliases.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            viewModel.Aliases.Add(alias);
        }

        foreach (var manualFolder in settings.ManualFolders.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            viewModel.ManualFolders.Add(manualFolder);
        }

        foreach (var documentUrl in settings.DocumentUrls.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            viewModel.DocumentUrls.Add(documentUrl);
        }

        return viewModel;
    }

    private void HookCollectionChanges()
    {
        ManualFolders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ManualFoldersSummary));
        DocumentUrls.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DocumentUrlsSummary));
        Aliases.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Aliases));
    }
}
