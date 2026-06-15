using System.Collections.ObjectModel;
using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed class ProductKnowledgeViewModel : ObservableObject
{
    private string productName = string.Empty;
    private string baseFolder = string.Empty;
    private string closeFolder = string.Empty;
    private bool isEnabled = true;

    public ProductKnowledgeViewModel()
    {
        HookCollectionChanges();
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

    public ObservableCollection<string> ManualFolders { get; } = [];

    public ObservableCollection<string> DocumentUrls { get; } = [];

    public bool IsEnabled
    {
        get => isEnabled;
        set => SetProperty(ref isEnabled, value);
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
            ProductName = ProductName?.Trim() ?? string.Empty,
            BaseFolder = BaseFolder?.Trim() ?? string.Empty,
            CloseFolder = CloseFolder?.Trim() ?? string.Empty,
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
        };
    }

    public static ProductKnowledgeViewModel FromSettings(ProductKnowledgeSettings settings)
    {
        var viewModel = new ProductKnowledgeViewModel
        {
            ProductName = settings.ProductName,
            BaseFolder = settings.BaseFolder,
            CloseFolder = settings.CloseFolder,
            IsEnabled = settings.IsEnabled,
        };

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
    }
}
