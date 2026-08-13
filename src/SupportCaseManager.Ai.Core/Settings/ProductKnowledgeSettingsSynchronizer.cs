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
        var selectedBeforeSync = FindByNameOrAlias(currentProducts, currentAiSettings.SelectedProductName)
            ?? FindByNameOrAlias(currentProducts, currentAiSettings.DefaultProductName);

        foreach (var supportProduct in supportToolProducts
                     .Where(static item => !string.IsNullOrWhiteSpace(item.ProductName))
                     .OrderBy(static item => item.SortOrder))
        {
            var currentProduct = supportProduct.Id == Guid.Empty
                ? null
                : currentProducts.FirstOrDefault(product => product.ProductId == supportProduct.Id);
            currentProduct ??= FindMatchingProduct(currentProducts, supportProduct);

            var synchronized = new ProductKnowledgeSettings
            {
                ProductId = supportProduct.Id == Guid.Empty
                    ? currentProduct?.ProductId ?? Guid.NewGuid()
                    : supportProduct.Id,
                ProductName = supportProduct.ProductName.Trim(),
                Aliases = NormalizeStringList(supportProduct.Aliases),
                BaseFolder = supportProduct.BaseFolder?.Trim() ?? string.Empty,
                CloseFolder = supportProduct.CloseFolder?.Trim() ?? string.Empty,
                ProductPromptFilePath = supportProduct.ProductPromptFilePath?.Trim() ?? string.Empty,
                IsEnabled = supportProduct.IsEnabled,
                SortOrder = supportProduct.SortOrder,
                ManualFolders = currentProduct?.ManualFolders ?? [],
                DocumentUrls = currentProduct?.DocumentUrls ?? [],
                CrawlMaxDepth = currentProduct?.CrawlMaxDepth ?? ProductKnowledgeSettings.DefaultCrawlMaxDepth,
                CrawlMaxPages = currentProduct?.CrawlMaxPages ?? ProductKnowledgeSettings.DefaultCrawlMaxPages,
            };

            if (currentProduct is null)
            {
                currentProducts.Add(synchronized);
            }
            else
            {
                currentProducts[currentProducts.IndexOf(currentProduct)] = synchronized;
            }
        }

        var products = currentProducts
            .OrderBy(static product => product.SortOrder)
            .ThenBy(static product => product.ProductName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var selectedAfterSync = selectedBeforeSync is null
            ? null
            : products.FirstOrDefault(product =>
                product.IsEnabled
                && product.ProductId != Guid.Empty
                && product.ProductId == selectedBeforeSync.ProductId);
        selectedAfterSync ??= FindByNameOrAlias(
            products.Where(static product => product.IsEnabled),
            currentAiSettings.SelectedProductName);
        selectedAfterSync ??= products.FirstOrDefault(static product => product.IsEnabled);

        var defaultAfterSync = FindByNameOrAlias(
                products.Where(static product => product.IsEnabled),
                currentAiSettings.DefaultProductName)
            ?? selectedAfterSync;

        return currentAiSettings with
        {
            Products = products,
            SelectedProductName = selectedAfterSync?.ProductName,
            DefaultProductName = defaultAfterSync?.ProductName,
        };
    }

    private static ProductKnowledgeSettings? FindMatchingProduct(
        IEnumerable<ProductKnowledgeSettings> products,
        SupportToolProductSettings supportProduct)
    {
        var names = supportProduct.Aliases
            .Append(supportProduct.ProductName)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return products.FirstOrDefault(product =>
            names.Contains(product.ProductName)
            || product.Aliases.Any(names.Contains));
    }

    private static ProductKnowledgeSettings? FindByNameOrAlias(
        IEnumerable<ProductKnowledgeSettings> products,
        string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return products.FirstOrDefault(product =>
            string.Equals(product.ProductName, name, StringComparison.OrdinalIgnoreCase)
            || product.Aliases.Any(alias => string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<ProductKnowledgeSettings> NormalizeCurrentProducts(AiAssistantSettings settings)
    {
        var seenIds = new HashSet<Guid>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in settings.Products.Where(static item => !string.IsNullOrWhiteSpace(item.ProductName)))
        {
            var productName = product.ProductName.Trim();
            if ((product.ProductId != Guid.Empty && !seenIds.Add(product.ProductId)) || !seenNames.Add(productName))
            {
                continue;
            }

            yield return product with
            {
                ProductName = productName,
                Aliases = NormalizeStringList(product.Aliases),
                BaseFolder = product.BaseFolder?.Trim() ?? string.Empty,
                CloseFolder = product.CloseFolder?.Trim() ?? string.Empty,
                ProductPromptFilePath = product.ProductPromptFilePath?.Trim() ?? string.Empty,
                ManualFolders = NormalizeStringList(product.ManualFolders),
                DocumentUrls = NormalizeStringList(product.DocumentUrls),
            };
        }

        if (seenNames.Count == 0 && !string.IsNullOrWhiteSpace(settings.ManualFolder))
        {
            var productName = settings.SelectedProductName ?? settings.DefaultProductName ?? "Default";
            yield return new ProductKnowledgeSettings
            {
                ProductId = Guid.NewGuid(),
                ProductName = productName.Trim(),
                BaseFolder = settings.BaseFolder ?? string.Empty,
                CloseFolder = settings.CloseFolder ?? string.Empty,
                ManualFolders = [settings.ManualFolder],
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
