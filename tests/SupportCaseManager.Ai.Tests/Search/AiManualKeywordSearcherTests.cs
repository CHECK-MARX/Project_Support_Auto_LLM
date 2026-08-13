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

    [Fact]
    public async Task SearchAsync_ProcedureNearNamedSubjectRanksAboveBroadMention()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateManual(
                "broad",
                "general.txt",
                "一般説明",
                $"CCT {new string('x', 180)} 生成方法を含む一般説明です。"),
            CreateManual(
                "procedure",
                "cct.txt",
                "CCT設定",
                "QA GUIで［CCTを生成］にチェックし、ビルドコマンドを指定して［同期］をクリックします。"),
        });
        var searcher = new AiManualKeywordSearcher();

        var results = await searcher.SearchAsync(temp.Path, "CCTの生成方法について教えてください。", maxResults: 8);

        Assert.True(results.Count >= 2);
        Assert.Equal("procedure", results[0].SourceId);
        Assert.Contains("procedureProximity=0.22", results[0].ScoreBreakdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_ValidateUploadProcedureRanksAbovePdfTableOfContents()
    {
        using var temp = new TempDirectory();
        await WriteIndexAsync(temp.Path, new[]
        {
            CreateManual(
                "toc",
                "Perforce_QAC_Manual.pdf",
                "Perforce_QAC_Manual",
                "Perforce Validateを使って作業する136 Validateの認証情報136 QA·GUIを使用して認証する136 QA·CLIを使用して認証する137 Validateからログオフする137 QA·GUIを使用してログオフする138 QA·CLIを使用してログオフする138 解析結果をValidateにアップロードする138 QA·GUIを使用して解析結果をアップロードする138 QA·CLIを使用して解析結果をアップロードする139 結合プロジェクトの作成とアップロード140 QA·GUIを使用して結合プロジェクトを作成する140 QA·CLIを使用して結合プロジェクトを作成する141"),
            CreateManual(
                "procedure",
                "Perforce_QAC_Manual.pdf",
                "Perforce_QAC_Manual",
                "QA·GUIからValidateに解析結果をアップロードするには以下のメニューを使用します。［ポータル］>［Validate］>［解析結果をアップロード］。QA·CLIではqacli validate build --qaf-project . を実行します。アップロードにはValidateでの認証、適切な権限、ビルドライセンスが必要です。"),
        });
        var searcher = new AiManualKeywordSearcher();

        var results = await searcher.SearchAsync(
            temp.Path,
            "QACで解析した結果をValidateへアップロードする方法を教えて。GUI及びCLIの方法も教えて。",
            maxResults: 8);

        Assert.Equal("procedure", results[0].SourceId);
        var toc = Assert.Single(results, static result => result.SourceId == "toc");
        Assert.Contains("tableOfContentsPenalty=-0.38", toc.ScoreBreakdown, StringComparison.Ordinal);
        Assert.DoesNotContain("tableOfContentsPenalty", results[0].ScoreBreakdown, StringComparison.Ordinal);
        Assert.Contains("qacli validate build", results[0].Text, StringComparison.OrdinalIgnoreCase);
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
