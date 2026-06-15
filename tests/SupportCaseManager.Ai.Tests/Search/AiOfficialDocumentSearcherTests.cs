using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Search;

public sealed class AiOfficialDocumentSearcherTests
{
    [Fact]
    public async Task SearchAsync_ReturnsOfficialDocSourcesForJapaneseQuery()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteOfficialIndexAsync(aiIndexFolder, "Checkmarx");
        var focus = new InquiryFocusExtractor().Extract("最新バージョンとEP/HFのリリース情報を確認したいです。");

        var results = await new AiOfficialDocumentKeywordSearcher().SearchAsync("Checkmarx", aiIndexFolder, focus, maxResults: 8);

        var result = Assert.Single(results);
        Assert.Equal("OfficialDoc", result.SourceType);
        Assert.Equal("Checkmarx", result.ProductName);
        Assert.Contains("Release Notes", result.Title);
        Assert.Contains("https://docs.example.test/release", result.Url);
        Assert.True(result.Score > 0);
    }

    [Fact]
    public async Task SearchAsync_PrioritizesDetectedTargetVersion()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteVersionedOfficialIndexAsync(aiIndexFolder, "Checkmarx");
        var focus = new InquiryFocusExtractor().Extract("CxSAST 9.6 のRelease NotesとHotfixを確認したいです。");

        var results = await new AiOfficialDocumentKeywordSearcher().SearchAsync("Checkmarx", aiIndexFolder, focus, maxResults: 8);

        Assert.NotEmpty(results);
        Assert.Contains("Release Notes for 9.6.0", results[0].Title);
        Assert.Contains("9.6", results[0].MatchedTerms);
        Assert.Contains("targetVersion=9.6", results[0].ScoreBreakdown);
    }

    private static async Task WriteOfficialIndexAsync(string aiIndexFolder, string productName)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
        Directory.CreateDirectory(productFolder);
        await using var stream = File.Create(Path.Combine(productFolder, AiOfficialDocumentIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, new AiOfficialDocumentIndexDocument
        {
            ProductName = productName,
            BuiltAt = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.FromHours(9)),
            SourceUrls = ["https://docs.example.test/release"],
            Documents =
            [
                new AiIndexedOfficialDocument
                {
                    Id = "official-release",
                    ProductName = productName,
                    Url = "https://docs.example.test/release",
                    Title = "Release Notes",
                    SectionTitle = "Latest Version",
                    Text = "最新バージョン 2026.1 EP3 HF2 のリリース情報と対応バージョンを確認できます。",
                    RetrievedAt = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.FromHours(9)),
                    ContentHash = "hash",
                },
            ],
        });
    }

    private static async Task WriteVersionedOfficialIndexAsync(string aiIndexFolder, string productName)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
        Directory.CreateDirectory(productFolder);
        await using var stream = File.Create(Path.Combine(productFolder, AiOfficialDocumentIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, new AiOfficialDocumentIndexDocument
        {
            ProductName = productName,
            BuiltAt = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.FromHours(9)),
            SourceUrls = ["https://docs.example.test/"],
            Documents =
            [
                new AiIndexedOfficialDocument
                {
                    Id = "official-97",
                    ProductName = productName,
                    Url = "https://docs.example.test/release-notes-9-7-0.html",
                    Title = "Release Notes for 9.7.0",
                    SectionTitle = "Release Notes",
                    Text = "CxSAST 9.7.0 Release Notes Hotfix Engine Pack.",
                    RetrievedAt = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.FromHours(9)),
                    ContentHash = "hash97",
                },
                new AiIndexedOfficialDocument
                {
                    Id = "official-96",
                    ProductName = productName,
                    Url = "https://docs.example.test/release-notes-9-6-0.html",
                    Title = "Release Notes for 9.6.0",
                    SectionTitle = "Release Notes",
                    Text = "CxSAST 9.6.0 Release Notes Hotfix Engine Pack.",
                    RetrievedAt = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.FromHours(9)),
                    ContentHash = "hash96",
                },
            ],
        });
    }
}
