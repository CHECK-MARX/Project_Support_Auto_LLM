using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Search;

public sealed class OfficialDocDiagnosticsTests
{
    [Fact]
    public void Build_ShowsDocumentUrlCount()
    {
        var text = OfficialDocDiagnosticsBuilder.Build(
            new ProductKnowledgeSettings
            {
                ProductName = "Checkmarx",
                DocumentUrls = ["https://docs.example.test/1", "https://docs.example.test/2"],
            },
            @"C:\ai-index",
            new InquiryFocus(),
            [],
            [],
            []);

        Assert.Contains("DocumentUrls登録数: 2", text);
    }

    [Fact]
    public void Build_ShowsIndexExistence()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, "Checkmarx");
        Directory.CreateDirectory(productFolder);
        File.WriteAllText(Path.Combine(productFolder, AiOfficialDocumentIndexBuilder.IndexFileName), "{}");

        var text = OfficialDocDiagnosticsBuilder.Build(
            new ProductKnowledgeSettings { ProductName = "Checkmarx", DocumentUrls = ["https://docs.example.test"] },
            aiIndexFolder,
            new InquiryFocus(),
            [],
            [],
            []);

        Assert.Contains("OfficialDoc index exists: true", text);
    }

    [Fact]
    public void Build_ShowsChunkCount()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, "Checkmarx");
        Directory.CreateDirectory(productFolder);
        File.WriteAllText(
            Path.Combine(productFolder, AiOfficialDocumentIndexBuilder.IndexFileName),
            """
            {
              "productName": "Checkmarx",
              "documents": [
                { "id": "1", "productName": "Checkmarx", "url": "https://docs.example.test", "title": "t", "text": "body", "retrievedAt": "2026-06-10T00:00:00+09:00", "contentHash": "abc" }
              ]
            }
            """);

        var text = OfficialDocDiagnosticsBuilder.Build(
            new ProductKnowledgeSettings { ProductName = "Checkmarx", DocumentUrls = ["https://docs.example.test"] },
            aiIndexFolder,
            new InquiryFocus(),
            [],
            [],
            []);

        Assert.Contains("OfficialDoc chunks: 1", text);
    }

    [Fact]
    public void Build_ShowsSearchSelectedAndWillSendCounts()
    {
        var searchResults = new[]
        {
            CreateSource("official-1", "OfficialDoc"),
            CreateSource("manual-1", "Manual"),
        };
        var selected = new[] { CreateSource("official-1", "OfficialDoc") };
        var willSend = new[] { CreateSource("official-1", "OfficialDoc") };

        var text = OfficialDocDiagnosticsBuilder.Build(
            new ProductKnowledgeSettings { ProductName = "Checkmarx", DocumentUrls = ["https://docs.example.test"] },
            @"C:\ai-index",
            new InquiryFocus { IsFreshnessSensitive = true, FreshnessReason = "最新を含むため" },
            searchResults,
            selected,
            willSend);

        Assert.Contains("OfficialDoc search results: 1", text);
        Assert.Contains("OfficialDoc selected: 1", text);
        Assert.Contains("OfficialDoc will send: 1", text);
        Assert.Contains("FreshnessSensitive: true", text);
    }

    [Fact]
    public void Build_WhenOfficialDocUnused_ShowsReasons()
    {
        var text = OfficialDocDiagnosticsBuilder.Build(
            new ProductKnowledgeSettings { ProductName = "Checkmarx", DocumentUrls = [] },
            @"C:\ai-index",
            new InquiryFocus { IsFreshnessSensitive = true },
            [],
            [],
            []);

        Assert.Contains("OfficialDocが使われていません。", text);
        Assert.Contains("DocumentUrls が未登録です", text);
    }

    private static SearchSource CreateSource(string id, string sourceType)
    {
        return new SearchSource
        {
            SourceId = id,
            SourceType = sourceType,
            Title = id,
            Text = "text",
            Score = 0.8,
        };
    }
}
