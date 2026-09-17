using SupportCaseManager.Core.Cases;

namespace SupportCaseManager.AiAssistant.App.GptHandoff;

internal enum GptHandoffImportExecutionStatus
{
    Cancelled,
    Rejected,
    Completed,
}

internal sealed record GptHandoffImportExecutionResult(
    GptHandoffImportExecutionStatus Status,
    string Message,
    bool PreviewShown,
    GptHandoffImportResult? ImportResult = null);

internal sealed class GptHandoffImportController
{
    private readonly Func<string> readClipboardText;
    private readonly Func<GptHandoffSnapshot, bool> confirmImport;
    private readonly Func<CaseRecord, GptHandoffSnapshot, CancellationToken, Task<GptHandoffImportResult>> importSnapshot;

    public GptHandoffImportController(
        Func<string> readClipboardText,
        Func<GptHandoffSnapshot, bool> confirmImport,
        Func<CaseRecord, GptHandoffSnapshot, CancellationToken, Task<GptHandoffImportResult>> importSnapshot)
    {
        this.readClipboardText = readClipboardText ?? throw new ArgumentNullException(nameof(readClipboardText));
        this.confirmImport = confirmImport ?? throw new ArgumentNullException(nameof(confirmImport));
        this.importSnapshot = importSnapshot ?? throw new ArgumentNullException(nameof(importSnapshot));
    }

    public async Task<GptHandoffImportExecutionResult> ExecuteAsync(
        CaseRecord caseRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseRecord);

        string clipboardText;
        try
        {
            clipboardText = readClipboardText();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Rejected($"Clipboardから引継ぎ情報を読み取れませんでした: {SafeMessage(ex)}");
        }

        if (!GptHandoffParser.TryParseClipboard(clipboardText, out var snapshot, out var parseError))
        {
            return Rejected(parseError);
        }

        bool confirmed;
        try
        {
            confirmed = confirmImport(snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Rejected($"GPT引継ぎ情報のプレビューを表示できませんでした: {SafeMessage(ex)}", previewShown: false);
        }

        if (!confirmed)
        {
            return new GptHandoffImportExecutionResult(
                GptHandoffImportExecutionStatus.Cancelled,
                "GPT引継ぎ情報の取り込みをキャンセルしました。",
                PreviewShown: true);
        }

        try
        {
            var result = await importSnapshot(caseRecord, snapshot, cancellationToken).ConfigureAwait(true);
            return new GptHandoffImportExecutionResult(
                GptHandoffImportExecutionStatus.Completed,
                result.Message,
                PreviewShown: true,
                result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Rejected($"GPT引継ぎ情報を保存できませんでした: {SafeMessage(ex)}", previewShown: true);
        }
    }

    private static GptHandoffImportExecutionResult Rejected(string message, bool previewShown = false) =>
        new(GptHandoffImportExecutionStatus.Rejected, message, previewShown);

    private static string SafeMessage(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return message.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
