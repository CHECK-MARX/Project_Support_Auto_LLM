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
        const string outputExample = """[{"sheet":"Sheet1","cell":"B4","sourceText":"原文","translatedText":"English translation"}]""";
        var instructions = LoadInstructions(context);
        var payload = entries.Select(static item => new
        {
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
            Keep sheet, cell, and sourceText exactly unchanged in the response.
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
            - sheet、cell、sourceTextは入力と完全一致させる。
            - 製品名、バージョン、エラーコード、コマンド、URL、メールアドレス、ファイルパスは変更しない。
            - Checkmarx案件では Path Traversal、Missing HSTS Header、CxSAST、Checkmarx、Source、Sink、Not Exploitable、Sanitizer、Query、Preset を維持する。
            - 翻訳不要と判断してもtranslatedTextへsourceTextをそのまま入れ、項目を省略しない。
            - Markdown、説明、コードフェンスを付けず、JSON配列だけを返す。

            ### 入力JSON
            {JsonSerializer.Serialize(payload)}

            ### 出力形式
            {outputExample}
            """;
    }

    public string ComposeTextTranslationPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ArtifactTextTranslationEntry> entries,
        ArtifactPromptContext context)
    {
        const string outputExample = """[{"key":"line:0","sourceText":"原文","translatedText":"English translation"}]""";
        var instructions = LoadInstructions(context);
        var payload = entries.Select(static item => new
        {
            key = item.Key,
            location = item.Location,
            sourceText = item.SourceText,
        });
        return $"""
            ## WPF成果物作成: {FormatName(plan.Format)}の英訳

            Translate every JSON item and return only a JSON array.
            Keep key and sourceText exactly unchanged.
            Preserve the file format, row/line structure, delimiters, quoting, and technical terms.
            Do not translate URLs, email addresses, file paths, command names, identifiers, or markup syntax.
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
            {outputExample}
            """;
    }

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

    private static string ComposeManufacturerQuestionPrompt(ManufacturerMailCaseContext context)
    {

        const string outputShape = "{\"japaneseDraft\":\"...\",\"englishDraft\":\"...\"}";
        var delta = context.CurrentCustomerDeltaContent.Count == 0
            ? "(添付ファイルの詳細を確認してください)"
            : string.Join(Environment.NewLine, context.CurrentCustomerDeltaContent.Select(static value => $"- {value}"));
        var followUp = context.IsFollowUp;
        var closeRule = context.CloseRequested
            ? "案件終了の依頼は、今回の追加質問に明示される場合だけ自然に扱ってよい。"
            : "案件終了の意図はありません。日本語・英語のどちらにも、クローズ、終了したい、案件を終了、customer would like to close、close this case、同義の表現を書かない。";
        var protectedValues = context.ProtectedValues.Values.Count == 0
            ? "- なし"
            : string.Join(Environment.NewLine, context.ProtectedValues.Values.Select(static value => $"- {value}"));

        return $"""
            ## メーカー向け確認メール案（日英）

            ファイル操作、メール送信、案件の再検索、ケース分類、履歴参照は行わない。
            以下の確定済みCase Contextだけを文章化し、日英の編集可能なメール案を1組作成する。

            ### Resolved Case Context
            Support ID: {context.SupportId}
            Product: {context.ProductName}
            Current Customer Delta: {context.CurrentCustomerDeltaFileName}
            Current Outbound Attachment: {context.CurrentOutboundAttachment}
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
            - {(followUp ? "前回のご回答へのお礼、お客様へ案内後に追加質問を受領した経緯、今回の添付の確認依頼を自然に含める。" : "今回の確認依頼として、添付の確認と回答を依頼する。")}
            - 前回メーカー回答の本文は渡されていない。内容を推測・要約しない。
            - Current Outbound Attachmentにある {context.CurrentOutboundAttachment} だけを今回の添付として書く。他の添付名を検索、推測、列挙しない。
            - Current Customer Deltaの主な技術論点を自然に1〜3文で扱い、添付ファイルの詳細確認を依頼する。
            - Canonical Protected Valuesは、日本語案・英語案の両方に表記を変えずに含める。
            - {closeRule}
            - 顧客の氏名、連絡先、ローカルパス、内部状態、RAG、CurrentCase Evidence、翻訳処理の診断を出力しない。
            - 自動送信、ファイル追記、案件ファイル変更は行わない。

            ### Required Structure
            日本語案: 件名、メーカー宛名、挨拶、Follow-upの経緯、主要論点、{context.CurrentOutboundAttachment}の案内、確認依頼、署名。
            英語案: Subject、Hello Support Team、挨拶、Follow-upの経緯、主要論点、{context.CurrentOutboundAttachment}の案内、確認依頼、Best regards署名。

            ### Output
            Markdown、説明、コードフェンスを付けず、次のJSONオブジェクトだけを返す。
            {outputShape}
            """;
    }

    private static string ComposeManufacturerAcknowledgementPrompt(ManufacturerMailCaseContext context)
    {
        const string outputShape = "{\"japaneseDraft\":\"...\",\"englishDraft\":\"...\"}";
        var recipient = string.IsNullOrWhiteSpace(context.ImmediateManufacturerRecipientName)
            ? "メーカーサポートご担当者様 / Hello Support Team"
            : $"{context.ImmediateManufacturerRecipientName}様 / Hi {context.ImmediateManufacturerRecipientName},";
        var protectedValues = context.ProtectedValues.Values.Count == 0
            ? "- なし"
            : string.Join(Environment.NewLine, context.ProtectedValues.Values.Select(static value => $"- {value}"));

        return $"""
            ## メーカー回答への返信案（日英）

            ファイル操作、メール送信、案件の再検索、ケース分類、履歴参照は行わない。
            以下の今回貼り付けられたメーカー回答だけを根拠に、日英の編集可能な受領・御礼メール案を1組作成する。

            ### Resolved Case Context
            Support ID: {context.SupportId}
            Product: {context.ProductName}
            Communication Intent: REPLY_TO_MANUFACTURER
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
        ArtifactFormat.Csv => "CSV (.csv)",
        ArtifactFormat.PlainText => "Text (.txt)",
        ArtifactFormat.Markdown => "Markdown (.md)",
        _ => "未対応形式",
    };
}
