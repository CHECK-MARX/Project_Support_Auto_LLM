using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Inquiries;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Search;

public sealed class ProductScopedSearchOfficialDocTests
{
    [Fact]
    public async Task SearchAllAsync_PrioritizesOfficialDocForFreshnessSensitiveQuery()
    {
        using var temp = new TempDirectory();
        var aiIndexFolder = Path.Combine(temp.Path, "ai-index");
        await WriteOfficialIndexAsync(aiIndexFolder, "Checkmarx");
        await WriteCaseIndexAsync(aiIndexFolder, "Checkmarx");
        var service = new ProductScopedSearchService(
            new AiCaseKeywordSearcher(),
            new AiManualKeywordSearcher(),
            new AiOfficialDocumentKeywordSearcher());
        var focus = new InquiryFocusExtractor().Extract("最新バージョンとEP/HFを確認したいです。");

        var results = await service.SearchAllAsync(
            new ProductKnowledgeSettings { ProductName = "Checkmarx" },
            aiIndexFolder,
            focus,
            maxResults: 8);

        Assert.True(focus.IsFreshnessSensitive);
        Assert.NotEmpty(results);
        Assert.Equal("OfficialDoc", results[0].SourceType);
    }

    private static async Task WriteOfficialIndexAsync(string aiIndexFolder, string productName)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
        Directory.CreateDirectory(productFolder);
        await using var stream = File.Create(Path.Combine(productFolder, AiOfficialDocumentIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, new AiOfficialDocumentIndexDocument
        {
            ProductName = productName,
            Documents =
            [
                new AiIndexedOfficialDocument
                {
                    Id = "official-latest",
                    ProductName = productName,
                    Url = "https://docs.example.test/latest",
                    Title = "Official Release",
                    SectionTitle = "Latest Version",
                    Text = "最新バージョン EP HF release version 情報です。",
                    RetrievedAt = DateTimeOffset.Now,
                    ContentHash = "official",
                },
            ],
        });
    }

    private static async Task WriteCaseIndexAsync(string aiIndexFolder, string productName)
    {
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(aiIndexFolder, productName);
        Directory.CreateDirectory(productFolder);
        await using var stream = File.Create(Path.Combine(productFolder, AiCaseIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, new AiIndexDocument
        {
            Notes =
            [
                new AiIndexedNote
                {
                    Id = "past-case",
                    SupportNumber = "00001234",
                    Title = "Past case latest",
                    NoteKind = "Note",
                    Text = "過去案件では最新バージョン EP HF と記載されていました。",
                },
            ],
        });
    }
}
