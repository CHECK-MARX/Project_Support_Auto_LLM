namespace SupportCaseManager.AiAssistant.App.ViewModels;

internal enum NaturalLanguageOperation
{
    NormalChat,
    ManufacturerAsk,
    ManufacturerReply,
    ManufacturerResponseTranslation,
}

internal static class NaturalLanguageOperationResolver
{
    public static NaturalLanguageOperation Resolve(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return NaturalLanguageOperation.NormalChat;
        }

        var text = instruction.Trim();
        var refersToManufacturerResponse = ContainsAny(
            text,
            "メーカー回答",
            "メーカーからの回答",
            "メーカーからの返答",
            "メーカーの回答",
            "このメーカー回答",
            "ベンダー回答",
            "ベンダーからの回答");

        if (refersToManufacturerResponse
            && ContainsAny(
                text,
                "御礼の返信",
                "お礼の返信",
                "受領の返信",
                "返信したい",
                "返信を作",
                "返信文を作",
                "返信メールを作",
                "返事を作"))
        {
            return NaturalLanguageOperation.ManufacturerReply;
        }

        if (refersToManufacturerResponse
            && ContainsAny(
                text,
                "日本語にしてください",
                "日本語にして",
                "日本語へ翻訳",
                "日本語に翻訳",
                "日本語訳して",
                "日本語訳してください",
                "和訳して",
                "和訳してください"))
        {
            return NaturalLanguageOperation.ManufacturerResponseTranslation;
        }

        var targetsManufacturer = ContainsAny(
            text,
            "メーカーへ",
            "メーカーに",
            "メーカーサポートへ",
            "メーカーサポートに",
            "ベンダーへ",
            "ベンダーに");
        var requestsConfirmation = ContainsAny(
            text,
            "確認したい",
            "確認する",
            "確認を依頼",
            "確認をお願い",
            "問い合わせたい",
            "問い合わせる",
            "質問したい",
            "質問する",
            "質問を");
        var requestsMail = ContainsAny(text, "メール", "メール文章", "メール文面");
        var requestsComposition = ContainsAny(
            text,
            "作成してください",
            "作成して",
            "作ってください",
            "作って",
            "作成をお願い",
            "文面を作",
            "文章を作",
            "メール案");

        return targetsManufacturer && requestsConfirmation && requestsMail && requestsComposition
            ? NaturalLanguageOperation.ManufacturerAsk
            : NaturalLanguageOperation.NormalChat;
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));
}
