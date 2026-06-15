using System.Text.Json;
using SupportCaseManager.Ai.Core.Indexing;
using SupportCaseManager.Ai.Core.Search;
using SupportCaseManager.Ai.Tests.Helpers;

namespace SupportCaseManager.Ai.Tests.Search;

public sealed class SearchScoreTests
{
    [Fact]
    public async Task ManualScores_AreNormalizedBetweenZeroAndOne()
    {
        using var temp = new TempDirectory();
        await WriteManualIndexAsync(temp.Path,
        [
            CreateManual("license", "license.md", "ライセンス認証エラー", "ライセンスサーバー名、ポート番号、ファイアウォール設定を確認します。"),
        ]);

        var results = await new AiManualKeywordSearcher().SearchAsync(
            temp.Path,
            "ライセンス認証エラーでライセンスサーバー名とポート番号を確認したいです。",
            maxResults: 8);

        var result = Assert.Single(results);
        Assert.InRange(result.Score ?? -1, 0.0, 1.0);
        Assert.NotEmpty(result.MatchedTerms);
        Assert.NotEmpty(result.QueryCoverage);
        Assert.NotEmpty(result.ScoreBreakdown);
    }

    [Fact]
    public async Task ManualScores_RankImportantQueryTermsAboveBoilerplate()
    {
        using var temp = new TempDirectory();
        await WriteManualIndexAsync(temp.Path,
        [
            CreateManual("target", "license_error_manual.md", "ライセンス認証エラー対応手順", "ライセンス認証に失敗する場合はライセンスサーバー名、ポート番号、ファイアウォール設定を確認します。"),
            CreateManual("boilerplate", "general.txt", "一般案内", "よろしくお願いします。お世話になっております。確認してください。"),
        ]);

        var results = await new AiManualKeywordSearcher().SearchAsync(
            temp.Path,
            "ライセンス認証エラーで製品が起動できません。ライセンスサーバー名、ポート番号、ファイアウォール設定を確認したいです。",
            maxResults: 8);

        var first = Assert.Single(results);
        Assert.Equal("target", first.SourceId);
        Assert.True((first.Score ?? 0) > 0.65);
    }

    [Fact]
    public async Task ManualScores_UnrelatedDocumentDoesNotReceiveHighScore()
    {
        using var temp = new TempDirectory();
        await WriteManualIndexAsync(temp.Path,
        [
            CreateManual("unrelated", "installer.md", "QACインストーラ", "インストールウィザードの起動方法を説明します。"),
        ]);

        var results = await new AiManualKeywordSearcher().SearchAsync(
            temp.Path,
            "ライセンス認証エラーでライセンスサーバー名とポート番号を確認したいです。",
            maxResults: 8);

        Assert.True(results.Count == 0 || (results[0].Score ?? 0) < 0.65);
    }

    [Fact]
    public async Task CaseScores_UseProductSupportNumberAndNoteKindMetadata()
    {
        using var temp = new TempDirectory();
        await WriteCaseIndexAsync(temp.Path,
        [
            CreateNote("target", "SUP-100", "Validate", "対応メモ", "ValidateアップロードエラーのProject admin権限を確認しました。"),
            CreateNote("other", "SUP-200", "QAC", "対応メモ", "QACインストーラの一般手順です。"),
        ]);

        var results = await new AiCaseKeywordSearcher().SearchAsync(
            temp.Path,
            "SUP-100 Validate アップロード エラー",
            maxResults: 8);

        Assert.True(results.Count >= 1);
        Assert.Equal("target", results[0].SourceId);
        Assert.InRange(results[0].Score ?? -1, 0.0, 1.0);
    }

    [Fact]
    public async Task ManualScores_RankValidateUploadDocumentAboveInstallerDocument()
    {
        using var temp = new TempDirectory();
        await WriteManualIndexAsync(temp.Path,
        [
            CreateManual("validate", "validate_upload.md", "Validateアップロードエラー", "Project admin権限とProjects root admin設定を確認します。"),
            CreateManual("installer", "qac_installer.md", "QACインストーラ", "グローバルライセンスとRCFのインストール手順です。"),
        ]);

        var results = await new AiManualKeywordSearcher().SearchAsync(
            temp.Path,
            "ValidateアップロードエラーでProject admin権限を確認したいです。",
            maxResults: 8);

        Assert.True(results.Count >= 1);
        Assert.Equal("validate", results[0].SourceId);
    }

    private static AiIndexedManual CreateManual(string id, string fileName, string title, string text)
    {
        return new AiIndexedManual
        {
            Id = id,
            FileName = fileName,
            FilePath = $@"D:\Manuals\{fileName}",
            Title = title,
            DocumentType = Path.GetExtension(fileName).Equals(".md", StringComparison.OrdinalIgnoreCase) ? "Markdown" : "Text",
            SectionTitle = title,
            Text = text,
        };
    }

    private static AiIndexedNote CreateNote(string id, string supportNumber, string title, string noteKind, string text)
    {
        return new AiIndexedNote
        {
            Id = id,
            SupportNumber = supportNumber,
            Title = title,
            NoteKind = noteKind,
            Text = text,
            NoteFilePath = $@"D:\Cases\{supportNumber}\note.txt",
            CaseFolderPath = $@"D:\Cases\{supportNumber}",
            CaseFolderName = supportNumber,
            CompanyName = "サンプル株式会社",
            Status = "対応中",
        };
    }

    private static async Task WriteManualIndexAsync(string aiIndexFolder, IReadOnlyList<AiIndexedManual> manuals)
    {
        Directory.CreateDirectory(aiIndexFolder);
        var document = new AiManualIndexDocument
        {
            BuiltAt = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.FromHours(9)),
            SourceFolder = @"D:\Manuals",
            Manuals = manuals,
        };

        await using var stream = File.Create(Path.Combine(aiIndexFolder, AiManualIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, document);
    }

    private static async Task WriteCaseIndexAsync(string aiIndexFolder, IReadOnlyList<AiIndexedNote> notes)
    {
        Directory.CreateDirectory(aiIndexFolder);
        var document = new AiIndexDocument
        {
            BuiltAt = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.FromHours(9)),
            SourceFolder = @"D:\Cases",
            Notes = notes,
        };

        await using var stream = File.Create(Path.Combine(aiIndexFolder, AiCaseIndexBuilder.IndexFileName));
        await JsonSerializer.SerializeAsync(stream, document);
    }
}
