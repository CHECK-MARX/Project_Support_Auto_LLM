namespace SupportCaseManager.AiAssistant.App.ViewModels;

internal enum NaturalLanguageOperation
{
    NormalChat,
    CustomerReply,
    CustomerStatusUpdate,
    ManufacturerAsk,
    ManufacturerReply,
    ManufacturerResponseTranslation,
}

internal enum NaturalLanguageRecipient
{
    Unspecified,
    Customer,
    Manufacturer,
}

internal readonly record struct NaturalLanguageIntentResolution(
    NaturalLanguageOperation Operation,
    NaturalLanguageRecipient Recipient,
    bool RequiresManufacturerResponse);

internal static class NaturalLanguageOperationResolver
{
    public static NaturalLanguageOperation Resolve(string? instruction) =>
        ResolveIntent(instruction).Operation;

    public static NaturalLanguageIntentResolution ResolveIntent(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return NormalChat();
        }

        var text = instruction.Trim();
        var targetsCustomer = ContainsAny(
            text,
            "お客様へ",
            "お客様への",
            "お客様向け",
            "お客様に",
            "おきゃくさまへ",
            "おきゃくさまへの",
            "顧客へ",
            "顧客への",
            "顧客向け");
        var targetsManufacturer = ContainsAny(
            text,
            "メーカーへ",
            "メーカーに",
            "メーカーサポートへ",
            "メーカーサポートに",
            "ベンダーへ",
            "ベンダーに");
        var refersToManufacturerResponse = ContainsAny(
            text,
            "メーカー回答",
            "メーカーからの回答",
            "メーカーからの返答",
            "メーカーの回答",
            "このメーカー回答",
            "ベンダー回答",
            "ベンダーからの回答");
        var requiresManufacturerResponse = refersToManufacturerResponse
            && ContainsAny(
                text,
                "以下のメーカー回答",
                "次のメーカー回答",
                "このメーカー回答",
                "メーカー回答を踏まえ",
                "メーカーからの回答を踏まえ",
                "メーカー回答に基づ",
                "メーカーからの回答に基づ",
                "メーカー回答の内容",
                "メーカー回答を要約",
                "メーカーからの回答を要約");

        var requestsTranslation = ContainsAny(
            text,
            "日本語にしてください",
            "日本語にして",
            "日本語へ翻訳",
            "日本語に翻訳",
            "日本語訳して",
            "日本語訳してください",
            "和訳して",
            "和訳してください");
        if (refersToManufacturerResponse && requestsTranslation)
        {
            return new(
                NaturalLanguageOperation.ManufacturerResponseTranslation,
                NaturalLanguageRecipient.Unspecified,
                true);
        }

        var requestsReply = ContainsAny(
            text,
            "御礼の返信",
            "お礼の返信",
            "受領の返信",
            "返信したい",
            "返信を作",
            "返信文を作",
            "返信メールを作",
            "返事を作");
        var explicitlyRepliesToManufacturer = ContainsAny(
            text,
            "メーカー回答へ返信",
            "メーカー回答に返信",
            "メーカー回答に御礼を返信",
            "メーカー回答にお礼を返信",
            "メーカーからの回答へ返信",
            "メーカーからの回答に返信",
            "メーカーへ御礼",
            "メーカーへお礼")
            || (targetsManufacturer && refersToManufacturerResponse && requestsReply);

        if (explicitlyRepliesToManufacturer && !targetsCustomer)
        {
            return new(
                NaturalLanguageOperation.ManufacturerReply,
                NaturalLanguageRecipient.Manufacturer,
                true);
        }

        var requestsCustomerCommunication = targetsCustomer && ContainsAny(
            text,
            "返信",
            "連絡",
            "案内",
            "メール",
            "文面",
            "文章",
            "回答を作",
            "回答案",
            "伝えて",
            "知らせて");
        if (requestsCustomerCommunication)
        {
            var isStatusUpdate = !requiresManufacturerResponse && ContainsAny(
                text,
                "依頼済み",
                "依頼を行いました",
                "確認中",
                "回答待ち",
                "まだ回答がありません",
                "回答となる",
                "回答になる",
                "回答予定",
                "回答見込み",
                "以降の回答",
                "進捗");
            return new(
                isStatusUpdate
                    ? NaturalLanguageOperation.CustomerStatusUpdate
                    : NaturalLanguageOperation.CustomerReply,
                NaturalLanguageRecipient.Customer,
                requiresManufacturerResponse);
        }

        if (refersToManufacturerResponse && requestsReply)
        {
            return new(
                NaturalLanguageOperation.ManufacturerReply,
                NaturalLanguageRecipient.Manufacturer,
                true);
        }

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
            ? new(
                NaturalLanguageOperation.ManufacturerAsk,
                NaturalLanguageRecipient.Manufacturer,
                false)
            : NormalChat();
    }

    private static NaturalLanguageIntentResolution NormalChat() =>
        new(
            NaturalLanguageOperation.NormalChat,
            NaturalLanguageRecipient.Unspecified,
            false);

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));
}
