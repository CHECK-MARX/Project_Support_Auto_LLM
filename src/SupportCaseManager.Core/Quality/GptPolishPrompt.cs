namespace SupportCaseManager.Core.Quality;

public static class GptPolishPrompt
{
    public static string Build(string audience, string draft, string? caseContext = null)
    {
        if (string.IsNullOrWhiteSpace(draft)) throw new ArgumentException("Draft is required.", nameof(draft));
        var direction = audience == QualityAudience.Customer
            ? "以下はAI回答支援で作成したお客様向け回答案です。日本企業向けサポートメールとして、自然さ、丁寧さ、簡潔さ、説明順序、読みやすさを改善してください。"
            : audience == QualityAudience.Manufacturer
                ? "以下はAI回答支援で作成したメーカー向け英語メール案です。自然な英語のサポートメールとして、丁寧さ、簡潔さ、説明順序、読みやすさを改善してください。"
                : throw new ArgumentException("Unsupported audience.", nameof(audience));
        var readinessInstruction = audience == QualityAudience.Customer
            ? "文章を整える前に、既存Readinessと提示された一次Evidenceを照合してください。正式サポート可否など重要事項が未確定、またはメーカー回答と公式資料に重要な差異がある場合は、完成したお客様向けメールを出さず、回答保留理由・不足情報・メーカーへ確認すべき具体的事項を示してください。known to workをofficially supportedと同一視しないでください。Readinessが未評価の場合は回答可能と推定しないでください。"
            : "メーカー向けメールの事実と技術値を保ち、案件情報にない事項を追加しないでください。";
        return $"{direction}\n\n案件情報にない事実を追加せず、製品仕様、バージョン、コマンド、パス、エラーコード、メーカー回答等の技術的意味を変更しないでください。ユーザーが明示した指示は維持してください。不要な質問、推測、案件終了意図は追加しないでください。\n{readinessInstruction}\n\n{caseContext?.Trim()}\n\n【Current draft】\n-----\n{draft.Trim()}\n-----";
    }
}
