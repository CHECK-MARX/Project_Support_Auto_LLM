using System.Text.Json;
using SupportCaseManager.Ai.Contracts;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Search;

public sealed class PastAnswerRetrievalTests
{
    [Fact]
    public void Extract_PairsQuestionAndReplyFromSeparateNotes()
    {
        var context = new CaseContext
        {
            ProductName = "HelixQAC",
            CompanyName = "株式会社サンプル",
            SupportNumber = "00001234",
            Notes =
            [
                CreateNote("お客様ご相談内容", "question.txt", "QAC 2025.1でE1234エラーが発生します。"),
                CreateNote("お客様への返信案", "reply.txt", "設定Aを有効にしてください。"),
                CreateNote("社内メモ", "memo.txt", "再現確認済み"),
            ],
        };

        var pair = Assert.Single(CaseAnswerPairExtractor.Extract(context, @"D:\Closed\00001234", "HelixQAC"));

        Assert.Equal("HelixQAC", pair.ProductName);
        Assert.Equal("00001234", pair.SupportNumber);
        Assert.Contains("E1234", pair.QuestionText, StringComparison.Ordinal);
        Assert.Equal("設定Aを有効にしてください。", pair.CustomerReplyText);
        Assert.Equal("再現確認済み", pair.InternalMemo);
        Assert.EndsWith("reply.txt", pair.SourceFile, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_RecognizesQuestionAndAnswerHeadingsInOneNote()
    {
        var context = new CaseContext
        {
            SupportNumber = "00001234",
            Notes =
            [
                CreateNote("Unknown", "combined.txt", """
                    お問い合わせ内容:
                    コマンド qacli --check でERR-42になります。
                    回答案:
                    設定名 analysis.mode を確認してください。
                    社内メモ:
                    検証済み
                    """),
            ],
        };

        var pair = Assert.Single(CaseAnswerPairExtractor.Extract(context, @"D:\Closed\00001234", "HelixQAC"));

        Assert.Contains("qacli --check", pair.QuestionText, StringComparison.Ordinal);
        Assert.Contains("analysis.mode", pair.CustomerReplyText, StringComparison.Ordinal);
        Assert.Equal("検証済み", pair.InternalMemo);
    }

    [Fact]
    public void Normalize_RemovesGreetingsCompanySignatureAndSupportNumber_ButKeepsTechnicalTerms()
    {
        var normalized = PastQuestionNormalizer.Normalize("""
            株式会社サンプル ご担当者様
            いつもお世話になっております。
            2026/07/19 サポート番号: 00001234
            Helix QAC 2025.1でERR-42が発生し、qacli --checkが失敗します。
            よろしくお願いいたします。
            support@example.com
            """, "株式会社サンプル");

        Assert.DoesNotContain("お世話", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("00001234", normalized, StringComparison.Ordinal);
        Assert.Contains("helix qac", normalized, StringComparison.Ordinal);
        Assert.Contains("2025.1", normalized, StringComparison.Ordinal);
        Assert.Contains("err-42", normalized, StringComparison.Ordinal);
        Assert.Contains("qacli --check", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_ReturnsCustomerReplyForExactQuestion()
    {
        using var temp = new TempDirectory();
        await WritePairIndexAsync(temp.Path, [CreatePair("HelixQAC", "同じ質問です。", "過去に送った回答です。")]);

        var result = Assert.Single(await new CaseAnswerPairSearcher().SearchAsync(temp.Path, "同じ質問です。"));

        Assert.Equal("ExactPastAnswer", result.SourceType);
        Assert.Equal(PastAnswerMatchKinds.Exact, result.MatchKind);
        Assert.Equal("過去に送った回答です。", result.Text);
        Assert.Equal(1, result.Score);
    }

    [Fact]
    public async Task SearchBySupportNumberAsync_ReturnsIndexedReplyForLoadedClosedCase()
    {
        using var temp = new TempDirectory();
        await WritePairIndexAsync(temp.Path,
        [
            CreatePair("HelixQAC", "案件に保存された問い合わせです。", "案件に保存された回答です。") with
            {
                SupportNumber = "00015391",
            },
        ]);

        var result = Assert.Single(await new CaseAnswerPairSearcher().SearchBySupportNumberAsync(
            temp.Path,
            "00015391"));

        Assert.Equal("ExactPastAnswer", result.SourceType);
        Assert.Equal(PastAnswerMatchKinds.SupportNumber, result.MatchKind);
        Assert.Equal("00015391", result.SupportNumber);
        Assert.Equal("案件に保存された回答です。", result.Text);
        Assert.Equal(1, result.Score);
    }

    [Fact]
    public async Task SearchAsync_MatchesWhenGreetingCompanyAndSignatureDiffer()
    {
        using var temp = new TempDirectory();
        var question = """
            株式会社A ご担当者様
            いつもお世話になっております。
            Helix QAC 2025.1でERR-42が発生します。
            よろしくお願いいたします。
            """;
        await WritePairIndexAsync(temp.Path, [CreatePair("HelixQAC", question, "設定Aをご確認ください。", "株式会社A")]);
        var query = """
            株式会社B ご担当者様
            お世話になっております。
            Helix QAC 2025.1でERR-42が発生します。
            user@example.com
            """;

        var result = Assert.Single(await new CaseAnswerPairSearcher().SearchAsync(temp.Path, query));

        Assert.Equal("ExactPastAnswer", result.SourceType);
        Assert.True(result.Score >= 0.90);
        Assert.Equal("設定Aをご確認ください。", result.Text);
    }

    [Fact]
    public async Task SearchAllAsync_PrioritizesExactPastAnswerForTroubleshooting()
    {
        using var temp = new TempDirectory();
        var productFolder = ProductIndexPathResolver.GetProductIndexFolder(temp.Path, "HelixQAC");
        Directory.CreateDirectory(productFolder);
        await WritePairIndexAsync(productFolder,
            [CreatePair("HelixQAC", "ERR-42エラーの解消方法を教えてください。", "設定Aを確認してください。")]);
        await using (var stream = File.Create(Path.Combine(productFolder, AiCaseIndexBuilder.IndexFileName)))
        {
            await JsonSerializer.SerializeAsync(stream, new AiIndexDocument
            {
                Notes = [new AiIndexedNote { Id = "note", Text = "ERR-42エラーの一般メモ", Title = "一般メモ" }],
            });
        }

        var results = await new ProductScopedSearchService(
            new AiCaseKeywordSearcher(),
            new AiManualKeywordSearcher()).SearchAllAsync(
                new ProductKnowledgeSettings { ProductName = "HelixQAC" },
                temp.Path,
                new InquiryFocus { FocusText = "ERR-42エラーの解消方法を教えてください。" });

        Assert.Equal("ExactPastAnswer", results[0].SourceType);
        Assert.Contains("設定A", results[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchPastAnswersAsync_DoesNotMixAnotherProduct()
    {
        using var temp = new TempDirectory();
        var checkmarxFolder = ProductIndexPathResolver.GetProductIndexFolder(temp.Path, "Checkmarx");
        Directory.CreateDirectory(checkmarxFolder);
        await WritePairIndexAsync(checkmarxFolder, [CreatePair("Checkmarx", "同じ質問", "別製品の回答")]);
        var service = new ProductScopedSearchService(new AiCaseKeywordSearcher(), new AiManualKeywordSearcher());

        var results = await service.SearchPastAnswersAsync(
            new ProductKnowledgeSettings { ProductName = "HelixQAC" },
            temp.Path,
            "同じ質問");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchPastAnswersAcrossProductsAsync_ReturnsOnlyExactMatches()
    {
        using var temp = new TempDirectory();
        var helixFolder = ProductIndexPathResolver.GetProductIndexFolder(temp.Path, "HelixQAC");
        var checkmarxFolder = ProductIndexPathResolver.GetProductIndexFolder(temp.Path, "Checkmarx");
        Directory.CreateDirectory(helixFolder);
        Directory.CreateDirectory(checkmarxFolder);
        await WritePairIndexAsync(helixFolder, [CreatePair("HelixQAC", "ERR-42が発生します。", "HelixQACの回答")]);
        await WritePairIndexAsync(checkmarxFolder, [CreatePair("Checkmarx", "別のライセンスエラーです。", "Checkmarxの回答")]);
        var service = new ProductScopedSearchService(new AiCaseKeywordSearcher(), new AiManualKeywordSearcher());

        var results = await service.SearchPastAnswersAcrossProductsAsync(
            [
                new ProductKnowledgeSettings { ProductName = "HelixQAC", IsEnabled = true },
                new ProductKnowledgeSettings { ProductName = "Checkmarx", IsEnabled = true },
            ],
            temp.Path,
            "ERR-42が発生します。");

        var result = Assert.Single(results);
        Assert.Equal("HelixQAC", result.ProductName);
        Assert.Equal("ExactPastAnswer", result.SourceType);
    }

    private static NoteSnapshot CreateNote(string kind, string fileName, string text)
    {
        return new NoteSnapshot
        {
            NoteKind = kind,
            FileName = fileName,
            FilePath = Path.Combine(@"D:\Closed\00001234", fileName),
            Text = text,
            LastModifiedAt = new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.FromHours(9)),
        };
    }

    private static CaseAnswerPair CreatePair(
        string product,
        string question,
        string answer,
        string? companyName = null)
    {
        var normalized = PastQuestionNormalizer.Normalize(question, companyName);
        return new CaseAnswerPair
        {
            Id = Guid.NewGuid().ToString("N"),
            ProductName = product,
            SupportNumber = "00001234",
            QuestionText = question,
            CustomerReplyText = answer,
            SourceFile = @"D:\Closed\00001234\reply.txt",
            UpdatedAt = new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.FromHours(9)),
            NormalizedQuestion = normalized,
            QuestionHash = PastQuestionNormalizer.Hash(normalized),
        };
    }

    private static async Task WritePairIndexAsync(string folder, IReadOnlyList<CaseAnswerPair> pairs)
    {
        Directory.CreateDirectory(folder);
        await using var stream = File.Create(Path.Combine(folder, CaseAnswerPairIndexDocument.FileName));
        await JsonSerializer.SerializeAsync(stream, new CaseAnswerPairIndexDocument
        {
            BuiltAt = DateTimeOffset.Now,
            Pairs = pairs,
        });
    }
}
