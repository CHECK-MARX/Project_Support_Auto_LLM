using System.Text.Json;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Search;

public class AiCaseKeywordSearcherTests
{
    [Fact]
    public async Task SearchAsync_ReturnsMatchingPastCaseNote()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateNote("n1", "00001234", "エラー対応", "起動時のエラーは設定ファイル確認で解消しました。"),
            CreateNote("n2", "00005678", "ログ確認", "ライセンス更新の問い合わせです。"),
        });
        var searcher = new AiCaseKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "起動 エラー", maxResults: 8);

        Assert.Single(results);
        Assert.Equal("n1", results[0].SourceId);
        Assert.Equal("PastCaseNote", results[0].SourceType);
        Assert.Contains("起動時のエラー", results[0].Text);
    }

    [Fact]
    public async Task SearchAsync_SupportNumberMatchRanksHigher()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateNote("n1", "00001234", "通常メモ", "一般的な説明です。"),
            CreateNote("n2", "00005678", "別案件メモ", "本文に 00001234 を含むだけです。"),
        });
        var searcher = new AiCaseKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "00001234", maxResults: 8);

        Assert.True(results.Count >= 2);
        Assert.Equal("n1", results[0].SourceId);
        Assert.True((results[0].Score ?? 0) >= (results[1].Score ?? 0));
    }

    [Fact]
    public async Task SearchAsync_TitleAndNoteKindCanMatch()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateNote("n1", "00001234", "メーカー連携内容", "本文は短いです。", noteKind: "メーカー連携内容"),
        });
        var searcher = new AiCaseKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "メーカー連携", maxResults: 8);

        Assert.Single(results);
        Assert.Equal("n1", results[0].SourceId);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task SearchAsync_RespectsMaxResults()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, Enumerable.Range(1, 5)
            .Select(index => CreateNote($"n{index}", $"0000000{index}", $"検索対象 {index}", "同じキーワードを含みます。")));
        var searcher = new AiCaseKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "キーワード", maxResults: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchAsync_MissingIndexReturnsEmptyList()
    {
        using var temp = new TempDirectory();
        var searcher = new AiCaseKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "エラー", maxResults: 8);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_MapsSearchSourceFields()
    {
        using var temp = new TempDirectory();
        var notePath = Path.Combine(temp.Path, "case", "note.txt");
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateNote("n1", "00001234", "障害対応", "エラーの根拠本文です。", noteFilePath: notePath),
        });
        var searcher = new AiCaseKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "根拠", maxResults: 8);

        var result = Assert.Single(results);
        Assert.Equal("n1", result.SourceId);
        Assert.Equal("PastCaseNote", result.SourceType);
        Assert.Equal("障害対応", result.Title);
        Assert.Equal(notePath, result.FilePath);
        Assert.Equal("00001234", result.SupportNumber);
        Assert.NotNull(result.Score);
    }

    [Fact]
    public async Task SearchAsync_SearchesJapaneseKeyword()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateNote("n1", "00001234", "日本語メモ", "プリンターの接続エラーを再起動で確認しました。"),
        });
        var searcher = new AiCaseKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "接続エラー", maxResults: 8);

        Assert.Single(results);
        Assert.Contains("接続エラー", results[0].Text);
    }

    private static AiIndexedNote CreateNote(
        string id,
        string supportNumber,
        string title,
        string text,
        string noteKind = "お客様ご相談内容",
        string? noteFilePath = null)
    {
        return new AiIndexedNote
        {
            Id = id,
            CaseFolderPath = @"D:\Cases\20260602(sample_00001234)対応中_20260602",
            CaseFolderName = "20260602(sample_00001234)対応中_20260602",
            CompanyName = "株式会社サンプル",
            SupportNumber = supportNumber,
            Status = "対応中",
            ReceptionDate = new DateOnly(2026, 6, 2),
            NoteKind = noteKind,
            NoteFilePath = noteFilePath ?? @"D:\Cases\note.txt",
            Title = title,
            Text = text,
            LastModifiedAt = new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.FromHours(9)),
        };
    }

    private static async Task WriteIndexAsync(string aiIndexFolder, IEnumerable<AiIndexedNote> notes)
    {
        Directory.CreateDirectory(aiIndexFolder);
        var document = new AiIndexDocument
        {
            BuiltAt = new DateTimeOffset(2026, 6, 2, 17, 15, 0, TimeSpan.FromHours(9)),
            SourceFolder = @"D:\Cases\Closed",
            Notes = notes.ToList(),
        };
        await using var stream = File.Create(Path.Combine(aiIndexFolder, AiCaseIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, document, new JsonSerializerOptions { WriteIndented = true });
    }
}
