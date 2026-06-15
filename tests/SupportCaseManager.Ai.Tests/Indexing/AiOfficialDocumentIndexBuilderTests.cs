using System.Net;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Facts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Indexing;

public sealed class AiOfficialDocumentIndexBuilderTests
{
    [Fact]
    public async Task BuildAsync_FetchesRegisteredUrlAndWritesOfficialIndex()
    {
        using var temp = new TempDirectory();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <html><head><title>Release Notes</title><script>ignore</script></head>
                <body><nav>menu</nav><h1>Latest Version</h1><p>Latest version 2026.1 EP3 HF2 is supported.</p></body></html>
                """, Encoding.UTF8, "text/html"),
        });
        var builder = new AiOfficialDocumentIndexBuilder(handler, () => new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.FromHours(9)));

        var result = await builder.BuildAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "Checkmarx",
                DocumentUrls = ["https://docs.example.test/release"],
            },
            Path.Combine(temp.Path, "ai-index"));

        Assert.Equal(1, result.SourceUrlCount);
        Assert.Equal(1, result.FetchSuccessCount);
        Assert.True(File.Exists(result.IndexFilePath));
        var document = await ReadIndexAsync(result.IndexFilePath);
        var indexed = Assert.Single(document.Documents);
        Assert.Equal("Checkmarx", indexed.ProductName);
        Assert.Equal("https://docs.example.test/release", indexed.Url);
        Assert.Contains("Latest Version", indexed.SectionTitle);
        Assert.Contains("2026.1", indexed.Text);
        Assert.DoesNotContain("ignore", indexed.Text);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(result.IndexFilePath)!, FactCatalogStore.VersionCatalogFileName)));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(result.IndexFilePath)!, FactCatalogStore.ProductFactsFileName)));
    }

    [Fact]
    public async Task BuildAsync_RecordsFetchFailuresWithoutThrowing()
    {
        using var temp = new TempDirectory();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var builder = new AiOfficialDocumentIndexBuilder(handler);

        var result = await builder.BuildAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "HelixQAC",
                DocumentUrls = ["https://docs.example.test/missing"],
            },
            Path.Combine(temp.Path, "ai-index"));

        Assert.Equal(0, result.FetchSuccessCount);
        Assert.Equal(1, result.FetchFailureCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("Status=404", StringComparison.Ordinal));
        Assert.True(File.Exists(result.IndexFilePath));
    }

    [Fact]
    public async Task BuildAsync_SkipsNonHttpUrls()
    {
        using var temp = new TempDirectory();
        var builder = new AiOfficialDocumentIndexBuilder(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await builder.BuildAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "Klocwork",
                DocumentUrls = ["file:///C:/manual.html"],
            },
            Path.Combine(temp.Path, "ai-index"));

        Assert.Equal(0, result.FetchSuccessCount);
        Assert.Equal(0, result.FetchFailureCount);
        Assert.Equal(1, result.SkippedUrlCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("Skipped seed URL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_CrawlsSameHostLinksFromSeedAndSkipsExternalAndBinaryLinks()
    {
        using var temp = new TempDirectory();
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            return path switch
            {
                "/" => HtmlResponse("""
                    <html><head><title>Docs Home</title></head><body>
                    <a href="/release-notes-9-7.html">Release Notes for 9.7.0</a>
                    <a href="/hotfixes-9-7.html">Hotfixes 9.7.0</a>
                    <a href="https://external.example.test/release-notes.html">External</a>
                    <a href="/download/manual.pdf">PDF</a>
                    </body></html>
                    """),
                "/release-notes-9-7.html" => HtmlResponse("""
                    <html><head><title>Release Notes for 9.7.0</title></head>
                    <body><main><h1>Release Notes for 9.7.0</h1><p>CxSAST 9.7.0 release notes with EP and HF information.</p></main></body></html>
                    """),
                "/hotfixes-9-7.html" => HtmlResponse("""
                    <html><head><title>Hotfixes 9.7.0</title></head>
                    <body><main><h1>Hotfixes 9.7.0</h1><p>CxSAST 9.7.0 hotfix list HF1 HF2.</p></main></body></html>
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        var builder = new AiOfficialDocumentIndexBuilder(handler, () => new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.FromHours(9)));

        var result = await builder.BuildAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "Checkmarx",
                DocumentUrls = ["https://docs.example.test/"],
            },
            Path.Combine(temp.Path, "ai-index"));

        Assert.Equal(1, result.SourceUrlCount);
        Assert.True(result.DiscoveredUrlCount >= 3);
        Assert.Contains("https://docs.example.test/release-notes-9-7.html", result.DiscoveredUrls);
        Assert.Contains("https://docs.example.test/hotfixes-9-7.html", result.DiscoveredUrls);
        Assert.DoesNotContain(result.DiscoveredUrls, url => url.Contains("external.example.test", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.DiscoveredUrls, url => url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.ImportantPageUrls, url => url.Contains("release-notes", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.IndexedChunkCount >= 2);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    private static async Task<AiOfficialDocumentIndexDocument> ReadIndexAsync(string indexFilePath)
    {
        await using var stream = File.OpenRead(indexFilePath);
        return await JsonSerializer.DeserializeAsync<AiOfficialDocumentIndexDocument>(stream)
            ?? throw new InvalidOperationException("Official document index could not be read.");
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            this.handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    private static HttpResponseMessage HtmlResponse(string html)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };
    }
}
