次のJSONスキーマに厳密に従ってください。
JSON以外の文章を返さないでください。
Markdown code fenceを返さないでください。
internalMemoは必ずstringにしてください。
evidence.sourceIdは上記「参照根拠」にあるsourceIdだけを使用してください。
提供されていないsourceIdを作らないでください。
各参照根拠について、問い合わせと直接関係するかを評価してから使ってください。
関係が弱い根拠はevidenceに含めず、internalMemoに「根拠として弱い」と記載してください。
selected evidenceに含まれていても、問い合わせと直接関係しない場合は回答本文の根拠にしないでください。
根拠から確認できないことは「確認できません」とし、「存在しない」「対応していない」と断定しないでください。
internalMemoには使用したsourceIdと、不足している確認事項を含めてください。
internalMemoは単独のsourceIdや「string」だけにせず、人間が読める社内向けメモにしてください。
根拠がない場合、evidenceは空配列にしてください。
不明なことは断定せず、needConfirmationsへ入れてください。
customerReplyDraftはサポートメール本文として丁寧で簡潔な日本語にしてください。
customerReplyDraftは、要点、確認できた内容、確認が必要な内容、次の対応を含めてください。
customerReplyDraftには署名を含めないでください。あいさつ文は固定テンプレート差し込み予定のため、過剰な定型挨拶は避けてください。
customerReply / internal memo / required checks / confidence / warnings の形式で出力してください。

# JSONスキーマ

{
  "customerReplyDraft": "string",
  "internalMemo": "string",
  "needConfirmations": [
    {
      "question": "string",
      "reason": "string",
      "priority": "High|Normal|Low"
    }
  ],
  "evidence": [
    {
      "sourceId": "string",
      "sourceType": "PastCaseNote|Manual|OfficialDoc|CuratedFactCatalog",
      "title": "string",
      "excerpt": "string",
      "filePath": "string",
      "supportNumber": "string",
      "relevance": 0.0
    }
  ],
  "confidence": 0.0,
  "warnings": ["string"]
}
