using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Indexing;

public interface IEmbeddingModelDigestResolver
{
    Task<string?> ResolveAsync(string endpoint, string model, CancellationToken cancellationToken = default);
}

public sealed class OllamaEmbeddingModelDigestResolver : IEmbeddingModelDigestResolver
{
    private readonly HttpClient httpClient;

    public OllamaEmbeddingModelDigestResolver(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<string?> ResolveAsync(
        string endpoint,
        string model,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            var response = await httpClient.GetFromJsonAsync<TagsResponse>(
                new Uri(baseUri, "api/tags"), cancellationToken);
            var match = response?.Models.FirstOrDefault(item => ModelNameMatches(item.Name, model));
            return string.IsNullOrWhiteSpace(match?.Digest) ? null : match.Digest;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return null;
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
