using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Llm;

public sealed class OllamaEmbeddingClient : IOllamaEmbeddingClient
{
    private const int TimeoutSeconds = 120;
    private readonly HttpClient httpClient;

    public OllamaEmbeddingClient()
        : this(new HttpClient())
    {
    }

    public OllamaEmbeddingClient(HttpMessageHandler handler)
        : this(new HttpClient(handler))
    {
    }

    public OllamaEmbeddingClient(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<IReadOnlyList<IReadOnlyList<float>>> EmbedAsync(
        string endpoint,
        string model,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Embedding model is required.", nameof(model));
        }

        if (inputs.Count == 0)
        {
            return [];
        }

        if (!TryBuildEmbedUri(endpoint, out var uri))
        {
            throw new InvalidOperationException("Ollama endpoint is invalid for /api/embed.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                uri,
                new { model, input = inputs },
                timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                throw new InvalidOperationException(
                    $"Ollama /api/embed returned HTTP {(int)response.StatusCode}. {Truncate(responseText)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            var payload = await JsonSerializer.DeserializeAsync<EmbedResponse>(stream, cancellationToken: timeoutCts.Token);
            var embeddings = payload?.Embeddings ?? [];
            if (embeddings.Count != inputs.Count || embeddings.Any(static vector => vector.Count == 0))
            {
                throw new InvalidOperationException(
                    $"Ollama /api/embed returned an invalid vector count. Expected={inputs.Count}; Actual={embeddings.Count}");
            }

            return embeddings;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Ollama /api/embed timed out after {TimeoutSeconds} seconds.",
                ex);
        }
    }

    private static bool TryBuildEmbedUri(string? endpoint, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        uri = new Uri(baseUri, "api/embed");
        return uri.Scheme is "http" or "https";
    }

    private static string Truncate(string value)
    {
        const int maxLength = 400;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private sealed record EmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public IReadOnlyList<IReadOnlyList<float>> Embeddings { get; init; } = [];
    }
}
