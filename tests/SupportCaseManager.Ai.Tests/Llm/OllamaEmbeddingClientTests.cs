using System.Net;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Core.Llm;

namespace SupportCaseManager.Ai.Tests.Llm;

public sealed class OllamaEmbeddingClientTests
{
    [Fact]
    public async Task EmbedAsync_PostsBatchToApiEmbedWithoutPullingModel()
    {
        Uri? requestUri = null;
        string? requestBody = null;
        var client = new OllamaEmbeddingClient(new StubHttpMessageHandler(async request =>
        {
            requestUri = request.RequestUri;
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"embeddings":[[1.0,0.0],[0.0,1.0]]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }));

        var vectors = await client.EmbedAsync(
            "http://localhost:11434/",
            "nomic-embed-text",
            ["first", "second"]);

        Assert.Equal("http://localhost:11434/api/embed", requestUri?.AbsoluteUri);
        Assert.Equal(2, vectors.Count);
        using var json = JsonDocument.Parse(requestBody!);
        Assert.Equal("nomic-embed-text", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("input").GetArrayLength());
        Assert.False(json.RootElement.TryGetProperty("pull", out _));
    }

    [Fact]
    public async Task EmbedAsync_RejectsMismatchedVectorCount()
    {
        var client = new OllamaEmbeddingClient(new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"embeddings":[[1.0]]}""", Encoding.UTF8, "application/json"),
            })));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(
            "http://localhost:11434",
            "nomic-embed-text",
            ["first", "second"]));

        Assert.Contains("Expected=2", exception.Message, StringComparison.Ordinal);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync)
        {
            this.sendAsync = sendAsync;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return sendAsync(request);
        }
    }
}
