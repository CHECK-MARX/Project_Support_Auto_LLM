using SupportCaseManager.AiAssistant.App.GptHandoff;
using SupportCaseManager.Core.Cases;

namespace SupportCaseManager.AiAssistant.App.Tests;

public sealed class GptHandoffImportControllerTests
{
    [Fact]
    public async Task ValidClipboard_ShowsPreviewAndSavesOnlyAfterConfirmation()
    {
        using var temp = new TemporaryCaseFolder();
        var previewCalls = 0;
        var controller = Controller(
            ClipboardText(),
            snapshot =>
            {
                previewCalls++;
                return true;
            });

        var result = await controller.ExecuteAsync(Case(temp.Path));

        Assert.Equal(GptHandoffImportExecutionStatus.Completed, result.Status);
        Assert.True(result.PreviewShown);
        Assert.Equal(1, previewCalls);
        Assert.Equal(GptHandoffImportStatus.Imported, result.ImportResult?.Status);
        Assert.True(File.Exists(Path.Combine(temp.Path, "GPT連携内容_00018303.txt")));
    }

    [Fact]
    public async Task PreviewCancelled_DoesNotCreateCaseFile()
    {
        using var temp = new TemporaryCaseFolder();
        var controller = Controller(ClipboardText(), _ => false);

        var result = await controller.ExecuteAsync(Case(temp.Path));

        Assert.Equal(GptHandoffImportExecutionStatus.Cancelled, result.Status);
        Assert.True(result.PreviewShown);
        Assert.False(File.Exists(Path.Combine(temp.Path, "GPT連携内容_00018303.txt")));
    }

    [Fact]
    public async Task SameSnapshotTwice_DoesNotAppendDuplicate()
    {
        using var temp = new TemporaryCaseFolder();
        var controller = Controller(ClipboardText(), _ => true);
        var caseRecord = Case(temp.Path);

        var first = await controller.ExecuteAsync(caseRecord);
        var second = await controller.ExecuteAsync(caseRecord);

        Assert.Equal(GptHandoffImportStatus.Imported, first.ImportResult?.Status);
        Assert.Equal(GptHandoffImportStatus.NoChange, second.ImportResult?.Status);
        Assert.Equal("前回取り込み済みです。新しい情報はありません。", second.Message);
        var saved = await File.ReadAllTextAsync(Path.Combine(temp.Path, "GPT連携内容_00018303.txt"));
        Assert.Equal(1, CountOccurrences(saved, "*****追記部_"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("markerのないClipboardテキスト")]
    public async Task InvalidClipboard_ReturnsReasonWithoutPreviewOrSave(string clipboardText)
    {
        using var temp = new TemporaryCaseFolder();
        var previewCalls = 0;
        var controller = Controller(
            clipboardText,
            _ =>
            {
                previewCalls++;
                return true;
            });

        var result = await controller.ExecuteAsync(Case(temp.Path));

        Assert.Equal(GptHandoffImportExecutionStatus.Rejected, result.Status);
        Assert.False(result.PreviewShown);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Equal(0, previewCalls);
        Assert.False(File.Exists(Path.Combine(temp.Path, "GPT連携内容_00018303.txt")));
    }

    [Fact]
    public async Task ClipboardReadFailure_ReturnsExplicitReasonWithoutPreviewOrSave()
    {
        using var temp = new TemporaryCaseFolder();
        var controller = new GptHandoffImportController(
            () => throw new InvalidOperationException("Clipboard is busy"),
            _ => true,
            (_, _, _) => throw new InvalidOperationException("Import must not run"));

        var result = await controller.ExecuteAsync(Case(temp.Path));

        Assert.Equal(GptHandoffImportExecutionStatus.Rejected, result.Status);
        Assert.Contains("Clipboardから引継ぎ情報を読み取れませんでした", result.Message, StringComparison.Ordinal);
        Assert.Contains("Clipboard is busy", result.Message, StringComparison.Ordinal);
        Assert.False(result.PreviewShown);
        Assert.False(File.Exists(Path.Combine(temp.Path, "GPT連携内容_00018303.txt")));
    }

    [Fact]
    public async Task PreviewFailure_ReturnsExplicitReasonWithoutSaving()
    {
        using var temp = new TemporaryCaseFolder();
        var controller = new GptHandoffImportController(
            ClipboardText,
            _ => throw new InvalidOperationException("Preview unavailable"),
            (_, _, _) => throw new InvalidOperationException("Import must not run"));

        var result = await controller.ExecuteAsync(Case(temp.Path));

        Assert.Equal(GptHandoffImportExecutionStatus.Rejected, result.Status);
        Assert.Contains("プレビューを表示できませんでした", result.Message, StringComparison.Ordinal);
        Assert.Contains("Preview unavailable", result.Message, StringComparison.Ordinal);
        Assert.False(result.PreviewShown);
        Assert.False(File.Exists(Path.Combine(temp.Path, "GPT連携内容_00018303.txt")));
    }

    [Fact]
    public async Task MismatchedCaseRegistration_DoesNotSaveToAnotherCase()
    {
        using var temp = new TemporaryCaseFolder();
        var caseRecord = Case(temp.Path);
        caseRecord.GptRegistration.SupportId = "00019999";

        var result = await Controller(ClipboardText(), _ => true).ExecuteAsync(caseRecord);

        Assert.Equal(GptHandoffImportStatus.Blocked, result.ImportResult?.Status);
        Assert.Contains("Support IDが一致しません", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(temp.Path, "GPT連携内容_00018303.txt")));
    }

    private static GptHandoffImportController Controller(
        string clipboardText,
        Func<GptHandoffSnapshot, bool> confirm) =>
        new(
            () => clipboardText,
            confirm,
            (caseRecord, snapshot, cancellationToken) =>
                new GptHandoffImportService(() => new DateTimeOffset(2026, 9, 16, 21, 30, 0, TimeSpan.FromHours(9)))
                    .ImportAsync(caseRecord, snapshot, cancellationToken));

    private static CaseRecord Case(string folderPath) => new(
        "Company",
        "00018303",
        "調査中",
        "20260916",
        Path.GetFileName(folderPath),
        folderPath,
        "2026-09-16T00:00:00Z",
        gptRegistration: new GptCaseRegistration
        {
            SupportId = "00018303",
            Product = "Checkmarx",
            TargetGptKey = "checkmarx",
            ConversationUrl = "https://chatgpt.com/c/existing",
            RegistrationState = GptRegistrationStates.Registered,
        });

    private static string ClipboardText() => $$"""
        {{GptHandoffFormat.StartMarker}}
        【メーカー担当者】
        Ivo
        【新たに判明した事項】
        設定手順を確認済み
        【現在の未解決事項】
        最終確認待ち
        【解決済みに変更した事項】
        なし
        【メーカー最新回答の要旨】
        回答あり
        【お客様対応上の注意事項】
        推測しない
        【現在の次アクション】
        メーカー回答を確認
        {{GptHandoffFormat.EndMarker}}
        """;

    private static int CountOccurrences(string value, string expected) =>
        value.Split(expected, StringSplitOptions.None).Length - 1;

    private sealed class TemporaryCaseFolder : IDisposable
    {
        public TemporaryCaseFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SupportCaseManagerTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
