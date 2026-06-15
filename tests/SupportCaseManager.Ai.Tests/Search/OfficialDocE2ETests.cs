using System.Net;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Answers;
using SupportCaseManager.Ai.Core.Evidence;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Llm;
using SupportCaseManager.Ai.Core.Prompts;
using SupportCaseManager.Ai.Core.Safety;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Search;

public sealed class OfficialDocE2ETests
{
    [Fact]
    public async Task BuildAsync_WithThreeMockUrls_WritesExpectedVersionData()
    {
        using var temp = new TempDirectory();
        var handler = new StubHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var html = url switch
            {
                var value when value.Contains("release-notes", StringComparison.OrdinalIgnoreCase) =>
                    """
                    <html><head><title>Release Notes for 9.7.0</title></head>
                    <body><main><h1>Release Notes for 9.7.0</h1><p>CxSAST version 9.7.0 is now available.</p></main></body></html>
                    """,
                var value when value.Contains("engine-pack", StringComparison.OrdinalIgnoreCase) =>
                    """
                    <html><head><title>Engine Pack Version 9.7.6</title></head>
                    <body><article><h1>Engine Pack Version 9.7.6</h1><p>Engine Pack (EP) version 9.7.6.</p></article></body></html>
                    """,
                _ =>
                    """
                    <html><head><title>9.7.0 Hotfixes</title></head>
                    <body><main><h1>9.7.0 Hotfixes</h1><p>HF10 May 2026 latest hotfix.</p></main></body></html>
                    """,
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            };
        });

        var builder = new AiOfficialDocumentIndexBuilder(handler);
        var result = await builder.BuildAsync(
            new ProductKnowledgeSettings
            {
                ProductName = "Checkmarx",
                DocumentUrls =
                [
                    "https://docs.checkmarx.com/en/release-notes-for-9-7-0.html",
                    "https://docs.checkmarx.com/en/engine-pack-version-9-7-6.html",
                    "https://docs.checkmarx.com/en/hotfixes-9-7-0.html",
                ],
            },
            Path.Combine(temp.Path, "ai-index"));

        Assert.Equal(3, result.SourceUrlCount);
        Assert.True(result.IndexedChunkCount > 0);
        var document = await ReadIndexAsync(result.IndexFilePath);
        Assert.Contains(document.Documents, doc => doc.Text.Contains("9.7.0", StringComparison.Ordinal));
        Assert.Contains(document.Documents, doc => doc.Text.Contains("9.7.6", StringComparison.Ordinal));
        Assert.Contains(document.Documents, doc => doc.Text.Contains("HF10", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchAllAsync_ForLatestVersionQuery_ReturnsOfficialDoc()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteCheckmarxOfficialIndexAsync(aiIndexFolder);
        var service = CreateSearchService();
        var focus = new InquiryFocusExtractor().Extract(
            "CxSASTの最新バージョンを教えてください。また、EPとHFの最新バージョンも教えてください。");

        var results = await service.SearchAllAsync(
            new ProductKnowledgeSettings { ProductName = "Checkmarx" },
            aiIndexFolder,
            focus,
            maxResults: 8);

        Assert.True(focus.IsFreshnessSensitive);
        Assert.Contains(results, source => source.SourceType == "OfficialDoc");
        Assert.Equal("OfficialDoc", results[0].SourceType);
    }

    [Fact]
    public async Task GenerateDraftAsync_WithOfficialDoc_DoesNotSuppressFreshnessAnswer()
    {
        var request = new AnswerDraftRequest
        {
            InquiryText = "CxSASTの最新バージョンを教えてください。EPとHFも教えてください。",
            InquiryFocus = new InquiryFocus
            {
                FocusText = "CxSASTの最新バージョンを教えてください。EPとHFも教えてください。",
                IsFreshnessSensitive = true,
                FreshnessReason = "最新 / EP / HF を含むため",
            },
            Sources =
            [
                new SearchSource
                {
                    SourceId = "official-release",
                    SourceType = "OfficialDoc",
                    Title = "Release Notes for 9.7.0",
                    Text = "CxSAST version 9.7.0",
                    Url = "https://docs.checkmarx.com/en/release-notes-for-9-7-0.html",
                    Score = 0.9,
                },
                new SearchSource
                {
                    SourceId = "official-ep",
                    SourceType = "OfficialDoc",
                    Title = "Engine Pack Version 9.7.6",
                    Text = "Engine Pack (EP) version 9.7.6",
                    Url = "https://docs.checkmarx.com/en/engine-pack-version-9-7-6.html",
                    Score = 0.88,
                },
                new SearchSource
                {
                    SourceId = "official-hf",
                    SourceType = "OfficialDoc",
                    Title = "9.7.0 Hotfixes",
                    Text = "Latest Hotfix: HF10 May 2026",
                    Url = "https://docs.checkmarx.com/en/hotfixes-9-7-0.html",
                    Score = 0.86,
                },
            ],
            Settings = new AiAssistantSettings(),
        };

        var service = new AiAnswerService(
            new PromptBuilder(),
            new EvidenceBuilder(),
            new SafetyRedactionService(),
            new StaticLlmClient("""
                {
                  "customerReplyDraft": "確認時点の公式情報では、CxSAST：9.7.0、Engine Pack（EP）：9.7.6、Hotfix（HF）：HF10 です。",
                  "internalMemo": "official-release / official-ep / official-hf",
                  "evidence": [],
                  "confidence": 0.85,
                  "warnings": []
                }
                """));

        var result = await service.GenerateDraftAsync(request);

        Assert.Contains("9.7.0", result.CustomerReplyDraft);
        Assert.Contains("9.7.6", result.CustomerReplyDraft);
        Assert.Contains("HF10", result.CustomerReplyDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("OfficialDocなし", StringComparison.Ordinal));
        Assert.True(result.Confidence > 0.35);
    }

    [Fact]
    public async Task GenerateDraftAsync_WithoutOfficialDoc_SuppressesFreshnessAnswer()
    {
        var request = new AnswerDraftRequest
        {
            InquiryText = "最新バージョンを教えてください。",
            InquiryFocus = new InquiryFocus
            {
                FocusText = "最新バージョンを教えてください。",
                IsFreshnessSensitive = true,
                FreshnessReason = "最新を含むため",
            },
            Sources =
            [
                new SearchSource
                {
                    SourceId = "case-1",
                    SourceType = "PastCaseNote",
                    Title = "Past case",
                    Text = "過去案件では9.6.0が最新でした。",
                    Score = 0.9,
                },
            ],
            Settings = new AiAssistantSettings(),
        };

        var service = new AiAnswerService(
            new PromptBuilder(),
            new EvidenceBuilder(),
            new SafetyRedactionService(),
            new StaticLlmClient("""
                {
                  "customerReplyDraft": "最新版は9.6.0です。",
                  "internalMemo": "case-1",
                  "evidence": [],
                  "confidence": 0.8,
                  "warnings": []
                }
                """));

        var result = await service.GenerateDraftAsync(request);

        Assert.Contains("公式", result.CustomerReplyDraft);
        Assert.Contains(result.Warnings, warning => warning.Contains("OfficialDocなし", StringComparison.Ordinal));
    }

    private static IProductScopedSearchService CreateSearchService()
    {
        return new ProductScopedSearchService(
            new AiCaseKeywordSearcher(),
            new AiManualKeywordSearcher(),
            new AiOfficialDocumentKeywordSearcher());
    }

    private static async Task WriteCheckmarxOfficialIndexAsync(string aiIndexFolder)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, "Checkmarx");
        Directory.CreateDirectory(productFolder);
        await using var stream = File.Create(Path.Combine(productFolder, AiOfficialDocumentIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, new AiOfficialDocumentIndexDocument
        {
            ProductName = "Checkmarx",
            Documents =
            [
                new AiIndexedOfficialDocument
                {
                    Id = "official-release",
                    ProductName = "Checkmarx",
                    Url = "https://docs.checkmarx.com/en/release-notes-for-9-7-0.html",
                    Title = "Release Notes for 9.7.0",
                    Text = "CxSAST version 9.7.0 release latest version",
                    RetrievedAt = DateTimeOffset.Now,
                    ContentHash = "release",
                },
                new AiIndexedOfficialDocument
                {
                    Id = "official-ep",
                    ProductName = "Checkmarx",
                    Url = "https://docs.checkmarx.com/en/engine-pack-version-9-7-6.html",
                    Title = "Engine Pack Version 9.7.6",
                    Text = "Engine Pack EP version 9.7.6 latest",
                    RetrievedAt = DateTimeOffset.Now,
                    ContentHash = "ep",
                },
                new AiIndexedOfficialDocument
                {
                    Id = "official-hf",
                    ProductName = "Checkmarx",
                    Url = "https://docs.checkmarx.com/en/hotfixes-9-7-0.html",
                    Title = "9.7.0 Hotfixes",
                    Text = "Latest Hotfix HF10 May 2026 hotfix HF",
                    RetrievedAt = DateTimeOffset.Now,
                    ContentHash = "hf",
                },
            ],
        });
    }

    private static SearchSource CreateSource(string id, string sourceType, double score)
    {
        return new SearchSource
        {
            SourceId = id,
            SourceType = sourceType,
            Title = id,
            Text = "text",
            Score = score,
        };
    }

    [Fact]
    public void FreshnessAutoSelector_PrioritizesOfficialDocWithLowerThreshold()
    {
        var official = CreateSource("official-1", "OfficialDoc", 0.55);
        var pastCase = CreateSource("case-1", "PastCaseNote", 0.95);

        Assert.True(FreshnessEvidenceAutoSelector.ShouldAutoSelect(official, isFreshnessSensitive: true, autoSelectMinimumScore: 0.65));
        Assert.Equal(0, FreshnessEvidenceAutoSelector.GetSourcePriority("OfficialDoc", freshnessSensitive: true));
        Assert.Equal(2, FreshnessEvidenceAutoSelector.GetSourcePriority("PastCaseNote", freshnessSensitive: true));
    }

    private static async Task<AiOfficialDocumentIndexDocument> ReadIndexAsync(string indexFilePath)
    {
        await using var stream = File.OpenRead(indexFilePath);
        return await JsonSerializer.DeserializeAsync<AiOfficialDocumentIndexDocument>(stream)
            ?? throw new InvalidOperationException("Index could not be read.");
    }

    private sealed class StaticLlmClient : ILlmClient
    {
        private readonly string response;

        public StaticLlmClient(string response)
        {
            this.response = response;
        }

        public Task<LlmGenerationResult> GenerateAsync(
            PromptMessages messages,
            LlmProviderSettings settings,
            bool disableThinking = true,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmGenerationResult { Content = response, DoneReason = "stop" });
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
