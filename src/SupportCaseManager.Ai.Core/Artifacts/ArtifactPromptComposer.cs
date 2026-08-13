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

    string ComposeManufacturerMailPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ExcelTranslationValue> translations,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames);
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
}

public sealed class ArtifactPromptComposer : IArtifactPromptComposer
{
    public const int TranslationBatchSize = 80;
    private readonly string applicationBaseDirectory;

    public ArtifactPromptComposer(string? applicationBaseDirectory = null)
    {
        this.applicationBaseDirectory = applicationBaseDirectory ?? AppContext.BaseDirectory;
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

    public string ComposeManufacturerMailPrompt(
        ArtifactCreationPlan plan,
        IReadOnlyList<ExcelTranslationValue> translations,
        ArtifactPromptContext context,
        IReadOnlyList<string> attachmentNames)
    {
        var instructions = LoadInstructions(context);
        var translationSummary = string.Join(
            Environment.NewLine,
            translations.Take(120).Select(static item => $"- {item.Sheet}!{item.Cell}: {item.TranslatedText}"));
        return $"""
            ## WPF成果物作成: メーカーサポート向け英語メール案

            ファイル操作やメール送信は行わず、編集可能なメール本文案だけを返してください。

            ### 共通指示
            {ValueOrFallback(instructions.CommonInstruction, "(共通指示なし)")}

            ### 製品別指示
            {ValueOrFallback(instructions.ProductInstruction, "(製品別指示なし)")}

            ### 案件情報
            製品: {context.ProductName}
            サポートID: {context.SupportId}
            お客様会社名: {context.CompanyName}
            お客様ご相談内容:
            {context.InquiryText}

            ### 確認した添付ファイル
            {string.Join(Environment.NewLine, attachmentNames.Select(static name => $"- {name}"))}

            ### 作成済み英訳Excel
            ファイル名: {Path.GetFileName(plan.OutputFullPath)}
            翻訳対象要素数: {translations.Count}
            翻訳内容:
            {translationSummary}

            ### 今回メーカーへ確認したい論点
            {context.UserInstruction}

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

    private SupportPromptLoadResult LoadInstructions(ArtifactPromptContext context)
    {
        return SupportPromptFileLoader.Load(
            context.ProductPromptFilePath,
            context.SupportToolSettingsFilePath,
            applicationBaseDirectory: applicationBaseDirectory);
    }

    private static string ValueOrFallback(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
