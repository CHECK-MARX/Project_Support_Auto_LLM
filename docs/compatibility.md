# 互換性ポリシー（C# 版）

## 文字コードと改行
- `config/user-settings.json` と `cases-index.json` は **UTF-8 (BOM なし)** で読み書きする
- ノート本文 (`*.txt`) は **UTF-8 (BOM なし)** で保存し、**CRLF** を改行として固定する
- 既存データ取り込み時は以下の順でデコードを試行する
  1) UTF-8
  2) UTF-8 (BOM あり)
  3) CP932
  4) Shift_JIS

## ノート追記フォーマット
- 追記ヘッダ: `*****追記部_YYYY/MM/DD HH:mm:ss(ステータス)******`
- 追記フッタ: `--------------------------------------------------`

## ログ出力
- 出力先: `logs/SupportCaseManager.log`
- レベル: `INFO / WARNING / ERROR / DEBUG`
- 形式: `YYYY-MM-DD HH:mm:ss [LEVEL] support_case_manager :: message`

## 互換性テスト方針
- README の受入テスト項目を C# 版でも踏襲
- 既存 `cases-index.json` / `user-settings.json` を入力しても意味的に同一の結果になることを保証する
