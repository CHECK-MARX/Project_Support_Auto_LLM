namespace SupportCaseManager.Ai.Core.Artifacts;

/// <summary>Turns allow-listed case material into a formal manufacturer-mail prompt.</summary>
public sealed class ManufacturerMailComposer
{
    public string ComposeBilingualPrompt(
        string formatName,
        string outputFileName,
        string translationSummary,
        ManufacturerSafeContext safe)
    {
        ArgumentNullException.ThrowIfNull(safe);
        var brief = ManufacturerMailBriefBuilder.Build(safe);

        var currentAttachments = safe.CurrentOutboundAttachments
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => Path.GetFileName(value) ?? string.Empty)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var safeOutputFileName = Path.GetFileName(outputFileName) ?? string.Empty;
        var currentAttachmentText = RenderLines(currentAttachments, "（今回の添付はありません）");
        var currentDeltaText = RenderLines(
            safe.CurrentCustomerDeltaTechnicalContent,
            "今回のお客様からの追加確認事項は特定できません。内容を推測してはいけません。");
        var priorResponseText = RenderLines(
            brief.PriorResponseSummaryPoints.Select(static point => point.Text),
            brief.PreviousManufacturerContactConfirmed && brief.PreviousCustomerReplyFound
                ? "前回メーカー回答本文は利用できません。回答内容を推測せず、今回の追加確認だけを依頼してください。"
                : "前回メーカー回答はありません。存在しない回答を作らないでください。");
        var protectedValues = RenderLines(
            safe.RequiredProtectedTechnicalValues,
            "（保護対象の技術値はありません）");
        const string outputExample = """{"japaneseDraft":"...","englishDraft":"..."}""";
        var recipient = safe.ManufacturerRecipient.IsResolved
            ? safe.ManufacturerRecipient.DisplayName
            : "メーカーサポートご担当者様 / Hello Support Team";
        var followUp = safe.DraftMode == ManufacturerDraftMode.FollowUp;

        return $"""
            ## WPF成果物作成: メーカー向け確認メール案（日英）

            あなたはメーカーサポートへ送る正式なビジネスメールの作成担当です。
            既に解決済みのCase Contextと以下の許可情報だけを文章化してください。案件ファイル、会話履歴、RAG、メーカー回答の再検索や再判定は行いません。
            今回の質問や論点を追加・削除・意味変更せず、前回メーカー回答本文が利用できない場合は内容を推測してはいけません。
            ファイル操作、保存、メール送信は行わず、編集可能な日本語案と英語案をJSONで返してください。
            この依頼は1回の応答で日英2案を作成する単一Turnです。

            ### ManufacturerMailBrief / resolved Case Context（唯一の文章化対象）
            Purpose: {brief.Purpose}
            OriginRole: {brief.OriginRole}
            RequestSource: {brief.RequestSource}
            RecipientRole: {brief.RecipientRole}
            FollowUpTrigger: {brief.FollowUpTrigger}
            MailMode: {(brief.IsAttachmentCentricFollowUp ? "ATTACHMENT_CENTRIC_FOLLOWUP" : brief.Mode.ToString().ToUpperInvariant())}
            モード: {(followUp ? "FOLLOW_UP（前回回答を踏まえた追加確認）" : "INITIAL（初回確認）")}
            製品: {ValueOrFallback(safe.ProductName, "確定情報なし")}
            製品バージョン: {brief.ProductVersion}
            サポートID: {ValueOrFallback(safe.SupportId, "確定情報なし")}
            ResolutionStatus: {safe.ManufacturerRecipient.ResolutionStatus}
            メーカー宛先候補: {recipient}
            CloseRequested: {(brief.CloseRequested ? "TRUE" : "FALSE")}
            PreviousManufacturerContact: {(brief.PreviousManufacturerContactConfirmed ? "TRUE" : "FALSE")}
            PreviousCustomerReply: {(brief.PreviousCustomerReplyFound ? "TRUE" : "FALSE")}

            ### Relevant Prior Manufacturer Response
            {priorResponseText}

            ### 今回のお客様からの追加確認事項（Current Customer Delta）
            {currentDeltaText}

            ### 今回の送付対象
            {currentAttachmentText}

            ### Forbidden Claims
            {RenderLines(brief.ForbiddenClaims, "なし")}
            これらの論点・主張を新規に追加してはいけません。添付名は今回の送付対象の完全一致集合だけです。

            ### REQUIRED_PROTECTED_VALUES
            MAIL_BODY_REQUIRED: {protectedValues}
            MAIL_BODY_OR_ATTACHMENT_REQUIRED: （今回の添付資料または本文で保持する値）
            これらのMAIL_BODY_OR_ATTACHMENT_REQUIRED値は、本文または今回の添付資料で保持してください。過去の添付名や過去案件の値を追加してはいけません。
            MissingBeforeSend: 0
            MissingBeforeSendCount: 0
            次のリテラルは、根拠があるため日英両案で完全一致させる技術値です。翻訳、言い換え、省略、大文字小文字の変更、記号の変更は禁止です。

            ### FORMAL MANUFACTURER MAIL WRITING CONTRACT
            Evidenceは「書いてよい事実」を決め、あなたはそれを正式なメール文章へ変換します。SOURCE MATERIALの見出し、役割名、件数、内部状態を本文へ転記しないでください。

            日本語案は、次の順序を基本に、複雑な問い合わせでは必要な背景を省略しないでください。
            1. 件名
            2. メーカー宛名。宛先が未解決なら「メーカーサポートご担当者様」
            3. 「お世話になっております。」
            4. 「東陽テクニカの伊藤です。」
            5. 前回回答への御礼（FOLLOW_UPの場合）
            6. 前回回答をお客様へ案内し、その結果追加確認を受領した経緯（FOLLOW_UPの場合）
            7. 前回回答のうち今回に直接関係する内容の自然な要約
            8. お客様の追加事情、希望、維持したい実装方針
            9. 質問理由を含む、メーカーが回答しやすい具体的な確認事項
            10. 今回の添付案内。今回の添付だけを記載
            11. 必要に応じた追加資料提供の案内
            12. 「どうぞよろしくお願いいたします。」
            13. 「株式会社東陽テクニカ」「伊藤 健」
            ATTACHMENT_CENTRIC_FOLLOWUPでは、詳細な質問を本文へ再掲せず、主要論点を1〜3文で要約し、Additional Questionsシートの確認を依頼してください。
            役割方向は CUSTOMER → TOYO → MANUFACTURER です。「メーカーからの要望」「メーカーからの追加確認事項」と書いてはいけません。
            CloseRequestedがFALSEの場合、「クローズ」「終了したい」「close this case」などの終了意図を日本語案・英語案のどちらにも書いてはいけません。

            英語案も日本語案と同じ情報密度と質問数にし、直訳調ではなく自然なメーカーサポート向け英語にしてください。
            Subject、宛先（Resolvedなら安全に解決された名前のFirstName、未解決ならHello Support Team）、挨拶、Toyo担当者紹介、前回回答への御礼、follow-upの経緯、関連する前回回答の要約、お客様の意図、質問理由、今回の添付案内、追加資料案内、締めを含めてください。
            英語末尾は次の署名にしてください。

            Best regards,
            Ken Ito
            Toyo Corporation
            ATTACHMENT_CENTRIC_FOLLOWUPでは、詳細な質問を本文へ再掲せず、主要論点を1〜3文で要約し、添付シートの確認を依頼してください。

            ### FOLLOW_UPの文章方針
            FOLLOW_UPでは、前回回答への御礼 → 前回回答をお客様へ案内 → その結果の追加質問 → {ValueOrFallback(safeOutputFileName, "今回の添付ファイル")}の案内 → 今回に関係する前回回答の要約 → 追加確認事項、の流れを自然な段落で構成してください。
            Prior Manufacturer Responseが利用できない場合は、その不在を本文で説明せず、前回回答の内容を創作しないでください。

            ### CONTENT SAFETY
            - 顧客個人情報や連絡先は記載しない。企業名も許可情報として必要な場合だけ使う。
            - 過去添付ファイルの一覧、ローカルパス、証跡の識別子、アプリ内部の役割名・生成メタデータは記載しない。
            - アプリ内部の不足状態、抽出処理、翻訳処理の診断説明を本文へ出さない。
            - 根拠のない製品バージョン、原因、再現結果、回答、納期を追加しない。不足情報から質問を新設しない。
            - 今回の送付対象はCurrent Outbound Attachmentに列挙されたファイルだけであり、過去ファイルを推測して追加しない。
            - 日本語と英語で同じ質問番号を使い、日本語で質問1、質問2、英語でQuestion 1、Question 2のように対応付ける。
            - ATTACHMENT_CENTRIC_FOLLOWUPでは質問番号の本文記載を必須にせず、添付の詳細質問を置き換えない主要論点要約を作る。
            - FOLLOW_UPでは、お客様から追加質問を受領し、その内容をメーカーへ確認する流れを明示する。メーカー起点の要望として書いてはいけない。
            - Support IDは件名に1回だけ記載し、本文では繰り返さない。
            - 保護値は値一覧だけで出力せず、意味のある本文の中で使用する。
            - メール本文にメタデータや生成過程の説明を付けない。

            自動送信・ファイル追記・案件ファイル変更は行いません。ユーザーが確認・編集してから使用します。

            ### OUTPUT
            Markdown、説明、コードフェンスを付けず、次のJSONオブジェクトだけを返してください。
            {outputExample}
            japaneseDraftとenglishDraftはどちらも空にせず、正式なメール本文全体を文字列として入れてください。
            """;
    }

    public string ComposeRepairPrompt(
        ManufacturerDraftPair pair,
        ManufacturerSafeContext safe,
        IReadOnlyList<string> qualityIssues)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(safe);

        var issues = qualityIssues.Count == 0
            ? "メール構造を再確認してください。"
            : string.Join(Environment.NewLine, qualityIssues.Select(static issue => $"- {issue}"));
        // Do not recycle a rejected draft as evidence for the repair.
        return ComposeBilingualPrompt(string.Empty, safe.CurrentOutboundAttachments.FirstOrDefault() ?? string.Empty,
            string.Empty, safe) + Environment.NewLine
            + "MAIL_QUALITY_INCOMPLETE: 同じBriefに基づく1回限りの修復。指摘箇所を修正してください。\n" + issues;
    }

    private static string RenderLines(IEnumerable<string> values, string emptyText)
    {
        var lines = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => $"- {value.Trim()}")
            .ToArray();
        return lines.Length == 0 ? emptyText : string.Join(Environment.NewLine, lines);
    }

    private static string ValueOrFallback(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
