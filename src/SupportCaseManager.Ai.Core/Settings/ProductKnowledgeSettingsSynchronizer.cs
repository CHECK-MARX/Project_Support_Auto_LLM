using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Settings;

public sealed class ProductKnowledgeSettingsSynchronizer : IProductKnowledgeSettingsSynchronizer
{
    public AiAssistantSettings Synchronize(
        AiAssistantSettings currentAiSettings,
        IReadOnlyList<SupportToolProductSettings> supportToolProducts)
    {
        ArgumentNullException.ThrowIfNull(currentAiSettings);
        supportToolProducts ??= [];

        var currentProducts = NormalizeCurrentProducts(currentAiSettings).ToList();
        var byName = currentProducts.ToDictionary(
            static product => product.ProductName.Trim(),
            static product => product,
            StringComparer.OrdinalIgnoreCase);
        var orderedNames = currentProducts
            .Select(static product => product.ProductName.Trim())
            .ToList();

        foreach (var supportProduct in supportToolProducts.Where(static item => !string.IsNullOrWhiteSpace(item.ProductName)))
        {
            var productName = supportProduct.ProductName.Trim();
            if (byName.TryGetValue(productName, out var currentProduct))
            {
                byName[productName] = currentProduct with
                {
                    ProductName = productName,
                    BaseFolder = supportProduct.BaseFolder,
                    CloseFolder = supportProduct.CloseFolder,
                };
            }
            else
            {
                orderedNames.Add(productName);
                byName[productName] = new ProductKnowledgeSettings
                {
                    ProductName = productName,
                    BaseFolder = supportProduct.BaseFolder,
                    CloseFolder = supportProduct.CloseFolder,
                    ManualFolders = [],
                    DocumentUrls = [],
                    IsEnabled = true,
                };
            }
        }

        var products = orderedNames
            .Where(name => byName.ContainsKey(name))
            .Select(name => byName[name])
            .ToList();

        var selectedProductName = currentAiSettings.SelectedProductName;
        if (string.IsNullOrWhiteSpace(selectedProductName))
        {
            selectedProductName = currentAiSettings.DefaultProductName;
        }

        if (string.IsNullOrWhiteSpace(selectedProductName) && products.Count > 0)
        {
            selectedProductName = products[0].ProductName;
        }

        return currentAiSettings with
        {
            Products = products,
            SelectedProductName = selectedProductName,
            DefaultProductName = string.IsNullOrWhiteSpace(currentAiSettings.DefaultProductName)
                ? selectedProductName
                : currentAiSettings.DefaultProductName,
        };
    }

    private static IEnumerable<ProductKnowledgeSettings> NormalizeCurrentProducts(AiAssistantSettings settings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in settings.Products.Where(static item => !string.IsNullOrWhiteSpace(item.ProductName)))
        {
            var productName = product.ProductName.Trim();
            if (seen.Add(productName))
            {
                yield return product with
                {
                    ProductName = productName,
                    BaseFolder = product.BaseFolder?.Trim() ?? string.Empty,
                    CloseFolder = product.CloseFolder?.Trim() ?? string.Empty,
                    ManualFolders = NormalizeStringList(product.ManualFolders),
                    DocumentUrls = NormalizeStringList(product.DocumentUrls),
                };
            }
        }

        if (seen.Count == 0 && !string.IsNullOrWhiteSpace(settings.ManualFolder))
        {
            var productName = settings.SelectedProductName
                ?? settings.DefaultProductName
                ?? "Default";

            yield return new ProductKnowledgeSettings
            {
                ProductName = productName.Trim(),
                BaseFolder = settings.BaseFolder ?? string.Empty,
                CloseFolder = settings.CloseFolder ?? string.Empty,
                ManualFolders = [settings.ManualFolder],
                DocumentUrls = [],
                IsEnabled = true,
            };
        }
    }

    private static IReadOnlyList<string> NormalizeStringList(IReadOnlyList<string>? values)
    {
        return values?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];
    }
}
