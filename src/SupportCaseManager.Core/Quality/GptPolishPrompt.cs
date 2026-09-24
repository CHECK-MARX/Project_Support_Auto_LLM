namespace SupportCaseManager.Core.Quality;

public static class GptPolishPrompt
{
    public static string Build(string audience, string draft)
    {
        if (string.IsNullOrWhiteSpace(draft)) throw new ArgumentException("Draft is required.", nameof(draft));
        var direction = audience == QualityAudience.Customer
            ? "以下はAI回答支援で作成したお客様向け回答案です。日本企業向けサポートメールとして、自然さ、丁寧さ、簡潔さ、説明順序、読みやすさを改善してください。"
            : audience == QualityAudience.Manufacturer
                ? "以下はAI回答支援で作成したメーカー向け英語メール案です。自然な英語のサポートメールとして、丁寧さ、簡潔さ、説明順序、読みやすさを改善してください。"
                : throw new ArgumentException("Unsupported audience.", nameof(audience));
        return $"{direction}\n\n案件情報にない事実を追加せず、製品仕様、バージョン、コマンド、パス、エラーコード、メーカー回答等の技術的意味を変更しないでください。ユーザーが明示した指示は維持してください。不要な質問、推測、案件終了意図は追加しないでください。\n\n【AI回答支援の初稿】\n-----\n{draft.Trim()}\n-----";
    }
}
