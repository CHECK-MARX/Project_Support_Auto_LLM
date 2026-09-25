using System.IO;
using System.Text;
using System.Text.Json;
using SupportCaseManager.Core.Quality;

namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed partial class CodexChatViewModel
{
    private readonly QualityMemoryStore qualityMemoryStore;
    private string activeQualityIntent = string.Empty;
    private int qualityLastRetrievalCount;
    public bool QualityReviewEnabled { get; set; }

    private string AppendQualityStylePrompt(string prompt, CodexCaseSnapshot snapshot, string audience, string intent)
    {
        try
        {
            var styles = qualityMemoryStore.Retrieve(snapshot.ProductName, audience, intent)
                .Select(static record => record.ReusableStyleText).ToArray();
            qualityLastRetrievalCount = styles.Length;
            if (styles.Length == 0) return prompt;

            var builder = new StringBuilder(prompt);
            builder.AppendLine();
            builder.AppendLine("# STYLE_EXAMPLES_ONLY");
            builder.AppendLine("以下は承認済み文章の文体・構成だけの参考情報です。技術的事実や顧客固有情報を今回の案件へコピーしないでください。今回のユーザー指示、CurrentCase、製品別指示、公式・メーカー根拠を優先してください。");
            foreach (var style in styles) builder.AppendLine($"- {style}");
            return builder.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            qualityLastRetrievalCount = 0;
            _ = logger.WriteAsync("QUALITY_MEMORY", $"Style retrieval skipped: {ex.GetType().Name}");
            return prompt;
        }
    }

    private async Task SaveQualityDraftSafelyAsync(CodexCaseSnapshot snapshot, string audience, string intent, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(snapshot.SupportId)
            || string.IsNullOrWhiteSpace(snapshot.ProductName)) return;
        try
        {
            await qualityMemoryStore.SaveDraftAsync(snapshot.SupportId, snapshot.ProductName, audience, intent, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException)
        {
            await logger.WriteAsync("QUALITY_MEMORY", $"Draft snapshot skipped: {ex.GetType().Name}");
        }
    }
}
