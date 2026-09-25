using System.IO;
using System.Text.Json;
using SupportCaseManager.Core.Quality;
using SupportCaseManager.Core.Cases;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed partial class MainViewModel
{
    private bool qualityReviewEnabled;
    private int qualityLastRetrievalCount;
    private string qualityReviewText = "OFF";
    private string gptPolishStatusText = "登録済みGPTへ現在の回答案を送信します。";

    public string GptPolishStatusText
    {
        get => gptPolishStatusText;
        private set => SetProperty(ref gptPolishStatusText, value);
    }

    public bool QualityReviewEnabled
    {
        get => qualityReviewEnabled;
        set
        {
            if (!SetProperty(ref qualityReviewEnabled, value)) return;
            if (Codex is not null) Codex.QualityReviewEnabled = value;
            QualityReviewText = value ? "待機中" : "OFF";
        }
    }

    public int QualityLastRetrievalCount
    {
        get => qualityLastRetrievalCount;
        private set => SetProperty(ref qualityLastRetrievalCount, value);
    }

    public string QualityReviewText
    {
        get => qualityReviewText;
        private set => SetProperty(ref qualityReviewText, value);
    }

    private void ReviewCustomerQualityDraft()
    {
        if (!QualityReviewEnabled || lastRequest is null) return;
        var styleResult = QualityStyleReviewer.Review(CustomerReplyDraft, lastRequest.UserInstruction,
            lastRequest.QualityStyleExamples?.Count ?? 0);
        QualityReviewText = lastResult?.AnswerQuality is { } quality
            ? $"{styleResult}{Environment.NewLine}既存Answer Quality Gate: {quality.Decision}"
            : styleResult;
    }

    private async Task SaveCustomerQualityDraftSafelyAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomerReplyDraft) || string.IsNullOrWhiteSpace(SupportNumber)
            || string.IsNullOrWhiteSpace(ProductName)) return;
        try
        {
            await qualityMemoryStore.SaveDraftAsync(
                SupportNumber, ProductName, QualityAudience.Customer, "CUSTOMER_REPLY", CustomerReplyDraft);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException)
        {
            await loggerFactory(EffectiveAiDataFolder()).LogWarningAsync(
                $"Quality draft snapshot skipped: {ex.GetType().Name}");
        }
    }

    internal Task GptPolishAsync(string audience) => audience == QualityAudience.Manufacturer
        ? GptPolishManufacturerAsync()
        : GptPolishCustomerAsync();

    internal Task GptPolishCustomerAsync() => GptPolishDraftAsync(
        QualityAudience.Customer, CustomerReplyDraft, "お客様向け回答案");

    internal Task GptPolishTechnicalAnswerAsync() => GptPolishTechnicalAnswerAsync(Codex?.TechnicalAnswer);

    internal Task GptPolishTechnicalAnswerAsync(string? currentDraft) => GptPolishDraftAsync(
        QualityAudience.Customer, currentDraft, "Codex技術回答案");

    internal Task GptPolishManufacturerAsync() => GptPolishManufacturerAsync(Codex?.EnglishManufacturerDraft);

    internal Task GptPolishManufacturerAsync(string? currentDraft) => GptPolishDraftAsync(
        QualityAudience.Manufacturer, currentDraft, "メーカー英語案");

    private async Task GptPolishDraftAsync(string audience, string? draft, string targetName)
    {
        GptPolishStatusText = $"{targetName}のGPT推敲を確認しています。";
        if (string.IsNullOrWhiteSpace(draft))
        {
            SetGptPolishStatus($"{targetName}がありません。先に{targetName}を作成してください。");
            return;
        }
        CaseRecord caseRecord;
        string error;
        try
        {
            if (!TryBuildRegisteredCase(out caseRecord, out error))
            {
                if (error == "現在案件のGPT登録状態を確認できません。")
                {
                    error = "GPT未登録です。";
                }
                SetGptPolishStatus($"{targetName}の推敲を中止しました。{error}");
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            SetGptPolishStatus($"{targetName}の推敲を中止しました。GPT登録案件を確認できませんでした: {ex.Message}");
            return;
        }
        if (!string.Equals(CaseNaming.NormalizeSupportNumber(caseRecord.SupportNumber),
                CaseNaming.NormalizeSupportNumber(SupportNumber), StringComparison.Ordinal))
        {
            SetGptPolishStatus($"{targetName}の推敲を中止しました。案件フォルダと画面のSupport IDが一致しないため、GPTへ送信していません。");
            return;
        }

        var snapshot = BuildCodexCaseSnapshot();
        if (!string.IsNullOrWhiteSpace(snapshot.CaseFolder) && Directory.Exists(snapshot.CaseFolder))
        {
            try
            {
                var refreshed = await noteSnapshotReader.ReadAllAsync(snapshot.CaseFolder);
                snapshot = snapshot with { Notes = refreshed };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await loggerFactory(EffectiveAiDataFolder()).LogWarningAsync(
                    $"Polish context note refresh failed: {ex.GetType().Name}");
            }
        }
        var capsule = AnswerContextCapsuleComposer.ComposePolish(
            snapshot, Codex?.GetManufacturerResponseForContext(snapshot));
        await loggerFactory(EffectiveAiDataFolder()).LogInfoAsync(
            capsule.DiagnosticSummary("POLISH_CONTEXT_V1"));
        var prompt = GptPolishPrompt.Build(audience, draft, capsule.Text);
        try
        {
            await gptPolishConversationService.SendMessageAsync(
                caseRecord.GptRegistration.ConversationUrl,
                prompt,
                expectedTargetName: caseRecord.GptRegistration.TargetGptDisplayName);
            SetGptPolishStatus($"{targetName}: GPTへ推敲依頼を送信しました。");
        }
        catch (Exception ex)
        {
            SetGptPolishStatus($"{targetName}のGPT推敲依頼は送信されませんでした: {ex.Message}");
        }
    }

    private void SetGptPolishStatus(string message)
    {
        GptPolishStatusText = message;
        StatusMessage = message;
    }
}
