using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

/// <summary>
/// Builds a disposable embedding index from an existing product index without
/// modifying that source index or the application's active retrieval path.
/// </summary>
public sealed class EmbeddingIndexStagingBuilder
{
    private readonly EmbeddingIndexUpdater updater;
    private readonly HttpClient httpClient;

    public EmbeddingIndexStagingBuilder(
        EmbeddingIndexUpdater? updater = null,
        HttpClient? httpClient = null)
    {
        this.updater = updater ?? new EmbeddingIndexUpdater();
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<EmbeddingIndexUpdateResult> BuildAsync(
        string productName,
        string sourceProductIndexFolder,
        string stagingRoot,
        string endpoint,
        string embeddingModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceProductIndexFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        if (!Directory.Exists(sourceProductIndexFolder))
        {
            throw new DirectoryNotFoundException($"Product source index was not found: {sourceProductIndexFolder}");
        }

        var stagingProductFolder = Path.Combine(stagingRoot, productName);
        Directory.CreateDirectory(stagingProductFolder);
        var digest = await ResolveModelDigestAsync(endpoint, embeddingModel, cancellationToken);
        return await updater.UpdateAsync(
            productName,
            stagingProductFolder,
            endpoint,
            embeddingModel,
            forceRebuild: true,
            cancellationToken,
            sourceProductIndexFolder,
            digest,
            sanitizeEmbeddingInput: true);
    }

    private async Task<string> ResolveModelDigestAsync(
        string endpoint,
        string embeddingModel,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri))
        {
            return string.Empty;
        }

        try
        {
            var response = await httpClient.GetFromJsonAsync<TagsResponse>(new Uri(baseUri, "api/tags"), cancellationToken);
            var model = response?.Models.FirstOrDefault(item => ModelNameMatches(item.Name, embeddingModel));
            return model?.Digest ?? string.Empty;
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
    }

    private static bool ModelNameMatches(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(left.Replace(":latest", string.Empty, StringComparison.OrdinalIgnoreCase), right, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(left, right.Replace(":latest", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);

    private sealed record TagsResponse
    {
        [JsonPropertyName("models")]
        public IReadOnlyList<ModelTag> Models { get; init; } = [];
    }

    private sealed record ModelTag
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("digest")]
        public string Digest { get; init; } = string.Empty;
    }
}
