using System.Text;
using System.Text.Json;
using SupportCaseManager.Core.Config;

namespace SupportCaseManager.Ai.Core.Artifacts;

public interface IArtifactPromptComposer
{
    string ComposeTranslationPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ExcelTranslationEntry> entries,
        ArtifactPromptContext context);

    string ComposeTextTranslationPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ArtifactTextTranslationEntry> entries,
        ArtifactPromptContext context);

    string ComposeManufacturerMailPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ExcelTranslationValue> translations,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames);

    string ComposeManufacturerMailPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ArtifactTextTranslationValue> translations,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames);

    string ComposeBilingualManufacturerMailPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ExcelTranslationValue> translations,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames);

    string ComposeBilingualManufacturerMailPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ArtifactTextTranslationValue> translations,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames);

    string ComposeBilingualManufacturerMailPrompt(
        string formatName,
        string outputFileName,
        string translationSummary,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames);

    string ComposeSimpleBilingualManufacturerMailPrompt(ManufacturerMailCaseContext context);
}

public sealed record ArtifactPromptContext
{
    public string ProductName { get; init; } = string.Empty;
    public string ProductPromptFilePath { get; init; } = string.Empty;
    public string SupportToolSettingsFilePath { get; init; } = string.Empty;
    public string SupportId { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    public string InquiryText { get; init; } = string.Empty;
    public string UserInstruction { get; init; } = string.Empty;
    public string CurrentCaseEvidenceReferences { get; init; } = string.Empty;
    public ManufacturerFollowUpScope FollowUpScope { get; init; } = new();
    public ManufacturerRecipient ManufacturerRecipient { get; init; } = new();
    public ManufacturerProtectedValueSet ProtectedValues { get; init; } = new();
    public ManufacturerSafeContext ManufacturerSafeContext { get; init; } = new();
}

public sealed class ArtifactPromptComposer : IArtifactPromptComposer
{
    public const int TranslationBatchSize = 80;
    private readonly string applicationBaseDirectory;
    private readonly ManufacturerMailComposer manufacturerMailComposer;

    public ArtifactPromptComposer(
        string? applicationBaseDirectory = null,
        ManufacturerMailComposer? manufacturerMailComposer = null)
    {
        this.applicationBaseDirectory = applicationBaseDirectory ?? AppContext.BaseDirectory;
        this.manufacturerMailComposer = manufacturerMailComposer ?? new ManufacturerMailComposer();
    }

    public string ComposeTranslationPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ExcelTranslationEntry> entries,
        ArtifactPromptContext context)
    {
        var instructions = LoadInstructions(context);
        var payload = entries.Select(static item => new
        {
            id = ArtifactTranslationBatchContract.Id(item),
            targetKind = item.TargetKind.ToString(),
            sheet = item.Sheet,
            cell = item.Cell,
            sourceText = item.SourceText,
        });
        return $"""
            ## WPF成果物作成: Excel文字列の英訳

            Translate every JSON item according to targetKind:
            - Cell: translate the Japanese cell text.
            - DrawingText: translate the complete drawing paragraph while preserving technical terms.
            - SheetName: return a concise unique English sheet name of 1-31 characters without []:*?/\.
            Use each supplied id to return the translation. WPF retains the original sheet, cell, and sourceText locally.
            Text baked into bitmap images is intentionally outside this JSON request.

            あなたは読み取り専用の翻訳担当です。ファイル操作、シェル操作、保存、名称変更は行わないでください。
            WPFがユーザー確認後にコピーへ反映するため、指定JSONだけを返してください。

            ### 共通指示
            {ValueOrFallback(instructions.CommonInstruction, "(共通指示なし)")}

            ### 製品別指示
            {ValueOrFallback(instructions.ProductInstruction, "(製品別指示なし)")}

            ### 案件
            製品: {context.ProductName}
            サポートID: {context.SupportId}
            会社名: {context.CompanyName}
            お客様ご相談内容:
            {context.InquiryText}

            ### 今回の依頼
            {context.UserInstruction}

            ### 翻訳規則
            - 日本語を自然で簡潔な技術英語へ翻訳する。
            - idは入力と完全一致させる。原文・シート名・セル名は応答に複写しない。
            - 製品名、バージョン、エラーコード、コマンド、URL、メールアドレス、ファイルパスは変更しない。
            - Checkmarx案件では Path Traversal、Missing HSTS Header、CxSAST、Checkmarx、Source、Sink、Not Exploitable、Sanitizer、Query、Preset を維持する。
            - 翻訳不要と判断してもtranslatedTextへsourceTextをそのまま入れ、項目を省略しない。
            - Markdown、説明、コードフェンスを付けず、JSON配列だけを返す。

            ### 入力JSON
            {JsonSerializer.Serialize(payload)}

            ### 出力形式
            Return exactly {entries.Count} objects in a JSON array, in input order.
            Each object must contain only id and translatedText (both strings).
            Copy the supplied id exactly; generate only translatedText. Do not return sourceText, sheet, cell, or targetKind.
            This input JSON is the complete scope of this batch. Do not add items from other batches, case context, or conversation history.
            Translate a sheet name only when its SheetName item is explicitly present in THIS input JSON.
            Common/product instructions and case context help translate the supplied text; they do not authorize additional targets.
            Return every input item exactly once, with no extra items, missing items, or explanations.
            """;
    }

    public string ComposeTextTranslationPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ArtifactTextTranslationEntry> entries,
        ArtifactPromptContext context)
    {
        var instructions = LoadInstructions(context);
        var payload = entries.Select(static item => new
        {
            id = ArtifactTranslationBatchContract.Id(item),
            key = item.Key,
            location = item.Location,
            sourceText = item.SourceText,
        });
        return $"""
            ## WPF成果物作成: {FormatName(plan.Format)}の英訳

            Translate every JSON item and return only a JSON array.
            Use each supplied id to return the translation. WPF retains the original key and sourceText locally.
            Preserve the file format, document structure, paragraph order, tables, headers, footers, formatting, and technical terms.
            For PowerPoint, each item is an editable text segment in a slide, table, notes, layout, master, chart title, or diagram. Preserve its meaning, identifiers, and technical values. Do not add text from images or embedded files.
            Do not translate URLs, email addresses, file paths, command names, options, API names, product names, versions, identifiers, code, field codes, or markup syntax.
            WPFがユーザー確認後に同じ拡張子のコピーへ反映するため、ファイル操作や保存は行わないでください。

            ### 共通指示
            {ValueOrFallback(instructions.CommonInstruction, "(共通指示なし)")}

            ### 製品別指示
            {ValueOrFallback(instructions.ProductInstruction, "(製品別指示なし)")}

            ### 案件
            製品: {context.ProductName}
            サポートID: {context.SupportId}
            会社名: {context.CompanyName}
            お客様ご相談内容:
            {context.InquiryText}

            ### 今回の依頼
            {context.UserInstruction}

            ### 入力JSON ({FormatName(plan.Format)})
            {JsonSerializer.Serialize(payload)}

            ### 出力形式
            Return exactly {entries.Count} objects in a JSON array, in input order.
            Each object must contain only id and translatedText (both strings).
            Copy the supplied id exactly; generate only translatedText. Do not return sourceText, key, or location.
            This input JSON is the complete scope of this batch. Do not add items from other batches, case context, or conversation history.
            Common/product instructions and case context help translate the supplied text; they do not authorize additional targets.
            Return every input item exactly once, with no extra items, missing items, or explanations.
            """;
    }

    public static string ComposeTranslationRepairPrompt(
        string originalPrompt,
        IReadOnlyList<string> validationErrors)
    {
        return $"""
            {originalPrompt}

            ### 翻訳結果の修正（1回限定）
            前回の結果は検証に失敗しました。次の不一致を修正してください。
            {JsonSerializer.Serialize(validationErrors)}
            上記の入力JSONだけを対象に、全項目を含む修正済みJSON配列を返してください。
            差分だけでなく全項目を返し、入力のidは一切変更しないでください。
            各項目はidとtranslatedTextだけにし、sourceTextなど原文情報は返さないでください。
            計画外の項目を追加せず、項目を省略・重複しないでください。
            ファイル操作は行わず、JSON以外の説明やコードフェンスは付けないでください。
            """;
    }

    public static string AppendManufacturerOutputContract(string prompt, ManufacturerMailCaseContext context) => $"""
        {prompt}
        英語案の説明文・項目名に日本語を混ぜない。技術値・実ファイル名は原表記を維持する。
        Support IDは件名末尾だけに記載する。本文の重複した総論・登録用説明は省略し、具体的な質問と添付案内を記載する。
        初回依頼の場合、過去回答へのお礼・顧客へ案内済み・再質問の経緯を作らない。

        ### 最終出力契約（日英両案に必須）
        - 件名とSubjectには製品名「{context.ProductName}」を含め、当社サポートID「{context.SupportId}」を件名末尾の丸括弧内に原表記で含める。
        - 既存の件名が入力にない場合は、この案件情報から具体的な件名を作成する。
        - 「既存件名を維持」「Keep the existing thread subject」などの仮文・記入指示をメール本文に出力しない。
        - 次の必須値は日本語案・英語案の両方へ原表記で含める。文体例や添付の説明を理由に省略しない。
        {string.Join(Environment.NewLine, context.ProtectedValues.Values.Select(value => "- " + value))}
        - 原文で確認できない事実、追加質問、終了依頼を新しく追加しない。
        - japaneseDraftとenglishDraftの2文字列を持つJSONオブジェクトだけを返す。
        """;

    public static string ComposeManufacturerRepairPrompt(string originalPrompt, ManufacturerDraftPair pair,
        IReadOnlyList<string> errors) => $"""
        {originalPrompt}

        ### 検証失敗の修正（1回限定）
        次の案は未確定です。元の案件情報と最終出力契約に従って、不一致を修正してください。
        検証結果: {JsonSerializer.Serialize(errors)}
        前回案: {JsonSerializer.Serialize(pair)}
        確認済み内容と技術値を保持し、新しい事実・質問・終了依頼は追加しないでください。
        修正済みjapaneseDraftとenglishDraftの両方をJSONで返してください。
        """;

    public string ComposeManufacturerMailPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ExcelTranslationValue> translations,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames)
    {
        var safe = context.ManufacturerSafeContext;
        var outboundAttachments = SafeAttachmentNames(safe, attachmentNames);
        return $"""
            ## WPF成果物作成: メーカーサポート向け英語メール案

            ファイル操作やメール送信は行わず、編集可能なメール本文案だけを返してください。

            ### メーカー向け許可情報
            {ComposeSafeContext(safe, outboundAttachments)}

            ### 作成済み英訳Excel
            ファイル名: {Path.GetFileName(plan.OutputFullPath)}
            翻訳対象要素数: {translations.Count}
            翻訳内容:
            {ComposeSafeArtifactContent(safe)}

            ### 今回メーカーへ確認したい論点
            {SafeInstruction(safe.RequestedTask)}

            ### メール規則
            - 宛名は不明のため「Hello Support Team,」で開始する。
            - 事象、環境、確認済み事項、質問を分かりやすい英語で整理する。
            - 根拠のない原因や断定を追加しない。
            - 添付する英訳Excelのファイル名を明記する。
            - 末尾は次の署名にする。

            Best regards,
            Ken Ito
            Toyo Corporation

            メール本文だけを返してください。自動送信はしません。
            """;
    }

    public string ComposeManufacturerMailPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ArtifactTextTranslationValue> translations,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames)
    {
        var safe = context.ManufacturerSafeContext;
        var outboundAttachments = SafeAttachmentNames(safe, attachmentNames);
        return $"""
            ## WPF成果物作成: メーカーサポート向け英語メール案

            ファイル操作やメール送信は行わず、編集可能なメール本文案だけを返してください。

            ### メーカー向け許可情報
            {ComposeSafeContext(safe, outboundAttachments)}

            ### 作成済み英訳ファイル
            ファイル名: {Path.GetFileName(plan.OutputFullPath)}
            形式: {FormatName(plan.Format)}
            翻訳内容:
            {ComposeSafeArtifactContent(safe)}

            ### 今回メーカーへ確認したい論点
            {SafeInstruction(safe.RequestedTask)}

            ### メール規則
            - 宛名は不明のため「Hello Support Team,」で開始する。
            - 事象、環境、確認済み事項、質問を分かりやすい英語で整理する。
            - 根拠のない原因や断定を追加しない。
            - 添付する英訳ファイルのファイル名を明記する。
            - 末尾は次の署名にする。

            {safe.ToyoSenderSignature}

            メール本文だけを返してください。自動送信はしません。
            """;
    }

    public string ComposeBilingualManufacturerMailPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ExcelTranslationValue> translations,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames)
    {
        var translationSummary = string.Join(
            Environment.NewLine,
            translations.Take(120).Select(static item => $"- {item.Sheet}!{item.Cell}: {item.TranslatedText}"));
        return ComposeBilingualManufacturerMailPrompt(
            plan,
            FormatName(plan.Format),
            translations.Count,
            Path.GetFileName(plan.OutputFullPath),
            translationSummary,
            context,
            attachmentNames);
    }

    public string ComposeBilingualManufacturerMailPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ArtifactTextTranslationValue> translations,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames)
    {
        var translationSummary = string.Join(
            Environment.NewLine,
            translations.Take(120).Select(static item => $"- {item.Key}: {item.TranslatedText}"));
        return ComposeBilingualManufacturerMailPrompt(
            plan,
            FormatName(plan.Format),
            translations.Count,
            Path.GetFileName(plan.OutputFullPath),
            translationSummary,
            context,
            attachmentNames);
    }

    public string ComposeBilingualManufacturerMailPrompt(
        string formatName,
        string outputFileName,
        string translationSummary,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames)
    {
        return ComposeBilingualManufacturerMailPrompt(
            plan: null,
            formatName,
            translationSummary.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length,
            outputFileName,
            translationSummary,
            context,
            attachmentNames);
    }

    public string ComposeSimpleBilingualManufacturerMailPrompt(ManufacturerMailCaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.CommunicationIntent == ManufacturerCommunicationIntent.ReplyToManufacturer
            ? ComposeManufacturerAcknowledgementPrompt(context)
            : ComposeManufacturerQuestionPrompt(context);
    }

    private string ComposeManufacturerQuestionPrompt(ManufacturerMailCaseContext context)
    {

        const string outputShape = "{\"japaneseDraft\":\"...\",\"englishDraft\":\"...\"}";
        var delta = context.CurrentCustomerDeltaContent.Count == 0
            ? "(添付ファイルの詳細を確認してください)"
            : string.Join(Environment.NewLine, context.CurrentCustomerDeltaContent.Select(static value => $"- {value}"));
        var followUp = context.IsFollowUp;
        var attachments = context.OutboundAttachments.Count == 0 ? "(添付なし)" : string.Join(", ", context.OutboundAttachments);
        var closeRule = context.CloseRequested
            ? "案件終了の依頼は、今回の追加質問に明示される場合だけ自然に扱ってよい。"
            : "案件終了の意図はありません。日本語・英語のどちらにも、クローズ、終了したい、案件を終了、customer would like to close、close this case、同義の表現を書かない。";
        var protectedValues = context.ProtectedValues.Values.Count == 0
            ? "- なし"
            : string.Join(Environment.NewLine, context.ProtectedValues.Values.Select(static value => $"- {value}"));
        var productInstruction = LoadProductInstruction(context);

        return $"""
            ## メーカー向け確認メール案（日英）

            ファイル操作、メール送信、案件の再検索、ケース分類、履歴参照は行わない。
            以下の確定済みCase Contextだけを文章化し、日英の編集可能なメール案を1組作成する。

            ### Resolved Case Context
            Support ID: {context.SupportId}
            Product: {context.ProductName}

            ### Product-specific Codex instruction
            {ValueOrFallback(productInstruction, "(製品別指示なし)")}
            Current Customer Delta: {context.CurrentCustomerDeltaFileName}
            Current Outbound Attachments: {attachments}
            Previous Manufacturer Contact: {(context.PreviousManufacturerContact ? "TRUE" : "FALSE")}
            Previous Customer Reply: {(context.PreviousCustomerReply ? "TRUE" : "FALSE")}
            CloseRequested: {(context.CloseRequested ? "TRUE" : "FALSE")}
            Manufacturer Follow-up Allowed: {(context.ManufacturerFollowupAllowed ? "TRUE" : "FALSE")}
            Customer Reply Allowed: {(context.CustomerReplyAllowed ? "TRUE" : "FALSE")}

            ### Current Customer Delta
            {delta}

            ### Canonical Protected Values
            {protectedValues}

            ### Writing Contract
            - CUSTOMER → TOYO → MANUFACTURER の方向で書く。
            - {(followUp ? "前回のご回答へのお礼、お客様へ案内後に追加質問を受領した経緯、今回の添付の確認依頼を自然に含める。" : "初回の確認依頼です。前回回答へのお礼、お客様へ回答済み、追加問い合わせ・再質問という経緯を作らない。")}
            - 前回メーカー回答の本文は渡されていない。内容を推測・要約しない。
            - Current Outbound Attachmentsにある実ファイルを全て案内する。一覧にないファイルは添付済みと書かない。
            - 添付一覧はファイルの存在を確認した一覧であり、内容解析済みという意味ではない。未読の添付内容を推測しない。
            - Current Customer Deltaの具体的な質問・選択肢を意味を変えずに整理する。「添付をご確認ください」だけに省略しない。質問事項と添付案内を中心にし、総論の重複を避ける。
            - Support IDは件名末尾だけに記載し、本文に「We received a request under support ID ...」のような重複した導入を書かない。
            - 英語案の説明文・項目名はすべて英語。日本語の「受付番号」は Customer reference 等へ訳す。技術値と実添付名だけは原表記を保持する。
            - Canonical Protected Valuesは、日本語案・英語案の両方に表記を変えずに含める。
            - {closeRule}
            - 顧客の氏名、連絡先、ローカルパス、内部状態、RAG、CurrentCase Evidence、翻訳処理の診断を出力しない。
            - 自動送信、ファイル追記、案件ファイル変更は行わない。

            ### Required Structure
            日本語案: 件名、メーカー宛名、挨拶、{(followUp ? "確認済みの追加問い合わせの経緯" : "初回確認事項")}、具体的質問、{attachments}の案内、確認依頼、署名。
            英語案: Subject、Hello Support Team、挨拶、{(followUp ? "確認済みの追加問い合わせの経緯" : "初回確認事項")}、具体的質問、{attachments}の案内、確認依頼、Best regards署名。

            ### Output
            Markdown、説明、コードフェンスを付けず、次のJSONオブジェクトだけを返す。
            {outputShape}
            """;
    }

    private string ComposeManufacturerAcknowledgementPrompt(ManufacturerMailCaseContext context)
    {
        const string outputShape = "{\"japaneseDraft\":\"...\",\"englishDraft\":\"...\"}";
        var recipient = string.IsNullOrWhiteSpace(context.ImmediateManufacturerRecipientName)
            ? "メーカーサポートご担当者様 / Hello Support Team"
            : $"{context.ImmediateManufacturerRecipientName}様 / Hi {context.ImmediateManufacturerRecipientName},";
        var protectedValues = context.ProtectedValues.Values.Count == 0
            ? "- なし"
            : string.Join(Environment.NewLine, context.ProtectedValues.Values.Select(static value => $"- {value}"));
        var productInstruction = LoadProductInstruction(context);

        return $"""
            ## メーカー回答への返信案（日英）

            ファイル操作、メール送信、案件の再検索、ケース分類、履歴参照は行わない。
            以下の今回貼り付けられたメーカー回答だけを根拠に、日英の編集可能な受領・御礼メール案を1組作成する。

            ### Resolved Case Context
            Support ID: {context.SupportId}
            Product: {context.ProductName}
            Communication Intent: REPLY_TO_MANUFACTURER

            ### Product-specific Codex instruction
            {ValueOrFallback(productInstruction, "(製品別指示なし)")}
            Recipient: {recipient}
            CloseRequested: FALSE

            ### Immediate Manufacturer Response
            {context.ImmediateManufacturerResponse}

            ### Canonical Protected Values
            {protectedValues}

            ### Writing Contract
            - メーカー回答を受領したことへの御礼と、回答から確認できる事実の簡潔な要約だけを書く。
            - 新しい技術質問、確認依頼、Question/質問番号、推測、追加論点、案件終了の意図を追加しない。
            - "Could you", "Please confirm", "ご確認ください", "教えてください"など、メーカーへ回答を求める表現を書かない。
            - Current Customer Delta、添付ファイル、翻訳成果物、案件フォルダ、CurrentCase Evidence、内部状態を出力しない。
            - Canonical Protected Valuesは、日本語案・英語案の両方に表記を変えずに含める。
            - 顧客の氏名、連絡先、ローカルパス、RAG、診断情報を出力しない。
            - 自動送信、ファイル追記、案件ファイル変更は行わない。

            ### Required Structure
            日本語案: 件名、メーカー宛名、挨拶、御礼、回答内容の要約、再度の御礼、署名。
            英語案: Subject、Hi/Hello、Thank you、We understand、Thank you again、Best regards署名。

            ### Output
            Markdown、説明、コードフェンスを付けず、次のJSONオブジェクトだけを返す。
            {outputShape}
            """;
    }

    private string ComposeBilingualManufacturerMailPrompt(
        ArtifactCreationPlan? plan,
        string formatName,
        int translationCount,
        string outputFileName,
        string translationSummary,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames)
    {
        var safe = context.ManufacturerSafeContext;
        return manufacturerMailComposer.ComposeBilingualPrompt(
            formatName,
            outputFileName,
            ComposeSafeArtifactContent(safe),
            safe);
    }

    private SupportPromptLoadResult LoadInstructions(ArtifactPromptContext context)
    {
        return SupportPromptFileLoader.Load(
            context.ProductPromptFilePath,
            context.SupportToolSettingsFilePath,
            applicationBaseDirectory: applicationBaseDirectory);
    }

    private string LoadProductInstruction(ManufacturerMailCaseContext context)
    {
        var loaded = LoadInstructions(new ArtifactPromptContext
        {
            ProductName = context.ProductName,
            ProductPromptFilePath = context.ProductPromptFilePath,
            SupportToolSettingsFilePath = context.SupportToolSettingsFilePath,
        });
        return loaded.ProductInstruction;
    }

    private static IReadOnlyList<string> SafeAttachmentNames(
        ManufacturerSafeContext safe,
        IReadOnlyList<string> fallback)
    {
        return safe.CurrentOutboundAttachments;
    }

    private static string ComposeSafeContext(
        ManufacturerSafeContext safe,
        IReadOnlyList<string> outboundAttachments)
    {
        var lines = new List<string>
        {
            $"製品: {safe.ProductName}",
            $"製品バージョン: {ValueOrFallback(safe.ProductVersion, "未確認")}",
            $"サポートID: {safe.SupportId}",
            $"メーカー案件ID: {ValueOrFallback(safe.ManufacturerCaseId, "未確認")}",
            $"メーカー照会番号: {ValueOrFallback(safe.ManufacturerReferenceId, "未確認")}",
            "今回の送付対象:",
        };
        lines.AddRange(outboundAttachments.Select(static name => $"- {name}"));
        lines.Add("今回の追加確認事項:");
        lines.AddRange(safe.CurrentCustomerDeltaTechnicalContent);
        lines.Add("前回メーカー回答のうち今回の確認に必要な背景:");
        lines.AddRange(safe.RelevantPriorManufacturerResponse);
        lines.Add("差出人:");
        lines.Add(safe.ToyoSenderSignature);
        return string.Join(Environment.NewLine, lines);
    }

    private static string ComposeSafeArtifactContent(ManufacturerSafeContext safe) => safe
        .CurrentOutboundArtifactTechnicalContent.Count == 0
        ? "(許可された翻訳内容なし)"
        : string.Join(Environment.NewLine, safe.CurrentOutboundArtifactTechnicalContent.Select(static line => $"- {line}"));

    private static string SafeInstruction(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "今回の追加確認事項を、許可された情報だけで整理してください。"
            : value.Trim();
    }

    private static string ValueOrFallback(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string FormatName(ArtifactFormat format) => format switch
    {
        ArtifactFormat.ExcelWorkbook => "Excel Workbook (.xlsx)",
        ArtifactFormat.WordDocument => "Word Document (.docx)",
        ArtifactFormat.PowerPointPresentation => "PowerPoint Presentation (.pptx)",
        ArtifactFormat.Csv => "CSV (.csv)",
        ArtifactFormat.PlainText => "Text (.txt)",
        ArtifactFormat.Markdown => "Markdown (.md)",
        _ => "未対応形式",
    };
}
