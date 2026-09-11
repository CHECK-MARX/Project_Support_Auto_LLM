namespace SupportCaseManager.AiAssistant.App.ViewModels;

public sealed record CodexPromptPreset(string Name, string Prompt, string? Key = null)
{
    public const string AskManufacturerKey = "ASK_MANUFACTURER";
    public const string ReplyToManufacturerKey = "REPLY_TO_MANUFACTURER";

    public string CanonicalKey => string.IsNullOrWhiteSpace(Key)
        ? Name switch
        {
            "メーカーへ確認・追加質問する" => AskManufacturerKey,
            "メーカー回答へ返信する（御礼・受領）" => ReplyToManufacturerKey,
            _ => string.Empty,
        }
        : Key;

    public const string ManufacturerConfirmationPrompt =
        "案件ファイルとCurrentCase Evidenceを確認し、メーカーへ確認すべき内容を日本語で整理した後、意味的に同一の英語メール案を作成してください。質問番号、製品名、バージョン、サポートID、技術値、コマンド、オプション、パス、URL、添付ファイル名は両言語で維持し、不明な点は推測せず質問として記載してください。";

    public const string ManufacturerReplyPrompt =
        "直近に貼り付けられたメーカー回答へ、日英の受領・御礼メール案を作成してください。新しい技術質問、確認依頼、推測、案件終了の意図は追加しないでください。";

    private const string LegacyJapaneseManufacturerPrompt =
        "メーカーへ技術確認するための日本語メール案を、事象、環境、確認済み事項、質問に分けて作成してください。";
    private const string LegacyEnglishManufacturerPrompt =
        "メーカーへ技術確認するための英語メール案を、事象、環境、確認済み事項、質問に分けて作成してください。";

    public override string ToString() => Name;

    public static string NormalizeLegacyPrompt(string? prompt)
    {
        var normalized = prompt?.Trim() ?? string.Empty;
        return normalized is "メーカー向け日本語確認案を作成"
            or "メーカー向け英語メールを作成"
            or LegacyJapaneseManufacturerPrompt
            or LegacyEnglishManufacturerPrompt
            ? ManufacturerConfirmationPrompt
            : normalized;
    }

    public static IReadOnlyList<CodexPromptPreset> Defaults { get; } =
    [
        new("案件全体を調査", "案件全体を調査し、事象、原因候補、根拠、推奨対応、追加確認事項を整理してください。"),
        new("ログを重点的に調査", "選択したログを重点的に調査し、重要なエラー、発生順序、原因候補、次の確認手順を示してください。"),
        new("スクリーンショットを確認", "選択したスクリーンショットを確認し、表示内容、エラー、設定値、技術的な示唆を整理してください。"),
        new("設定ファイルを確認", "選択した設定ファイルを確認し、問い合わせに関係する設定、矛盾、注意点を整理してください。"),
        new("過去案件と比較", "選択された過去案件の根拠と現在の案件を比較し、共通点、相違点、再利用できる対応を整理してください。"),
        new("お客様への追加確認事項を作成", "不足情報を整理し、お客様へ確認する質問を優先度順に、回答しやすい日本語で作成してください。"),
        new("お客様向け回答案を作成", "調査結果と根拠だけを使い、会社名とお客様名を含む丁寧なメール形式のお客様向け回答案を作成してください。"),
        new("メーカーへ確認・追加質問する", ManufacturerConfirmationPrompt, AskManufacturerKey),
        new("メーカー回答へ返信する（御礼・受領）", ManufacturerReplyPrompt, ReplyToManufacturerKey),
        new("メーカー回答をお客様向けに変換", "メーカー回答の技術内容を変えず、お客様向けの丁寧な日本語メールへ変換してください。不確かな点は分離してください。"),
        new("技術内容を維持して最終レビュー", "技術値、コマンド、設定名、パス、URL、エラーコードを変更せず、回答漏れと日本語表現を最終レビューしてください。"),
    ];
}
