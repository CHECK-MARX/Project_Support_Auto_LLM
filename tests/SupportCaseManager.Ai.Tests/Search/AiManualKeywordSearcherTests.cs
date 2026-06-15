using System.Text.Json;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Search;

public class AiManualKeywordSearcherTests
{
    [Fact]
    public async Task SearchAsync_FindsManualChunkMatchingQueryText()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateManual("m1", "setup.txt", "セットアップ", "接続エラーの確認手順です。"),
            CreateManual("m2", "license.txt", "ライセンス", "更新手順です。"),
        });
        var searcher = new AiManualKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "接続エラー", maxResults: 8);

        var result = Assert.Single(results);
        Assert.Equal("m1", result.SourceId);
        Assert.Equal("Manual", result.SourceType);
        Assert.Contains("接続エラー", result.Text);
    }

    [Fact]
    public async Task SearchAsync_TitleMatchRanksHigher()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateManual("title", "setup.txt", "接続エラー", "本文は一般説明です。"),
            CreateManual("body", "other.txt", "一般", "本文に接続エラーがあります。"),
        });
        var searcher = new AiManualKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "接続エラー", maxResults: 8);

        Assert.True(results.Count >= 2);
        Assert.Equal("title", results[0].SourceId);
        Assert.True((results[0].Score ?? 0) >= (results[1].Score ?? 0));
    }

    [Fact]
    public async Task SearchAsync_SectionMatchRanksHigher()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateManual("section", "manual.md", "manual - ログ採取", "本文は短いです。", sectionTitle: "ログ採取"),
            CreateManual("body", "manual.md", "manual - その他", "本文にログ採取があります。", sectionTitle: "その他"),
        });
        var searcher = new AiManualKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "ログ採取", maxResults: 8);

        Assert.True(results.Count >= 2);
        Assert.Equal("section", results[0].SourceId);
    }

    [Fact]
    public async Task SearchAsync_RespectsMaxResults()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, Enumerable.Range(1, 5)
            .Select(index => CreateManual($"m{index}", $"manual{index}.txt", $"検索対象 {index}", "同じキーワードを含みます。")));
        var searcher = new AiManualKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "キーワード", maxResults: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchAsync_MissingIndexReturnsEmptyList()
    {
        using var temp = new TempDirectory();
        var searcher = new AiManualKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "接続", maxResults: 8);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_MapsSearchSourceFields()
    {
        using var temp = new TempDirectory();
        var filePath = Path.Combine(temp.Path, "manuals", "setup.md");
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateManual("m1", "setup.md", "setup - 起動エラー", "根拠本文です。", filePath: filePath, sectionTitle: "起動エラー"),
        });
        var searcher = new AiManualKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "根拠", maxResults: 8);

        var result = Assert.Single(results);
        Assert.Equal("m1", result.SourceId);
        Assert.Equal("Manual", result.SourceType);
        Assert.Equal("setup - 起動エラー", result.Title);
        Assert.Equal(filePath, result.FilePath);
        Assert.Null(result.SupportNumber);
        Assert.NotNull(result.Score);
    }

    [Fact]
    public async Task SearchAsync_SearchesJapaneseKeyword()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateManual("m1", "printer.md", "プリンター設定", "プリンターの接続確認と再起動手順です。"),
        });
        var searcher = new AiManualKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "接続確認", maxResults: 8);

        Assert.Single(results);
        Assert.Contains("接続確認", results[0].Text);
    }

    [Fact]
    public async Task SearchAsync_FindsJapaneseLicenseErrorManual()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateManual(
                "license-manual",
                "license_error_manual.md",
                "license_error_manual - ライセンス認証エラー対応手順",
                """
                # ライセンス認証エラー対応手順

                ライセンス認証に失敗する場合は、以下を確認します。

                1. ライセンスサーバー名が正しいこと
                2. ポート番号が正しいこと
                3. ファイアウォールで通信が遮断されていないこと
                4. クライアントPCからライセンスサーバーへ疎通できること
                """,
                sectionTitle: "ライセンス認証エラー対応手順"),
        });
        var searcher = new AiManualKeywordSearcher();

        var results = await searcher.SearchAsync(
            temp.Path,
            """
            ライセンス認証エラーで製品が起動できません。
            ライセンスサーバー名、ポート番号、ファイアウォール設定を確認したいです。
            """,
            maxResults: 8);

        Assert.NotEmpty(results);
        var result = results[0];
        Assert.Equal("Manual", result.SourceType);
        Assert.True((result.Score ?? 0) > 0);
        Assert.Contains("ライセンス", result.Title + result.Text);
    }

    private static AiIndexedManual CreateManual(
        string id,
        string fileName,
        string title,
        string text,
        string sectionTitle = "",
        string? filePath = null)
    {
        return new AiIndexedManual
        {
            Id = id,
            FilePath = filePath ?? $@"D:\Manuals\{fileName}",
            FileName = fileName,
            Title = title,
            DocumentType = Path.GetExtension(fileName).Equals(".md", StringComparison.OrdinalIgnoreCase) ? "Markdown" : "Text",
            SectionTitle = sectionTitle,
            Text = text,
            LastModifiedAt = new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.FromHours(9)),
        };
    }

    private static async Task WriteIndexAsync(string aiIndexFolder, IEnumerable<AiIndexedManual> manuals)
    {
        Directory.CreateDirectory(aiIndexFolder);
        var document = new AiManualIndexDocument
        {
            BuiltAt = new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.FromHours(9)),
            SourceFolder = @"D:\Manuals",
            Manuals = manuals.ToList(),
        };

        await using var stream = File.Create(Path.Combine(aiIndexFolder, AiManualIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, document, new JsonSerializerOptions { WriteIndented = true });
    }
}
