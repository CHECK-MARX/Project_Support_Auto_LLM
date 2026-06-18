# SupportCaseManager / AI回答支援

サポート案件管理用の既存WPFアプリと、独立したAI回答支援WPFアプリを含む .NET 10 ソリューションです。

既存案件データ、ノート、`cases-index.json`、`user-settings.json` は既存アプリ側の管理対象です。AI回答支援の設定・ログ・インデックス・ドラフトは `ai-data` / `ai-index` 配下に分離します。

---

## プロジェクト構成

| プロジェクト | 役割 |
| --- | --- |
| `src/SupportCaseManager.App` | 既存のサポート案件管理WPFアプリ |
| `src/SupportCaseManager.Core` | 案件、ノート、設定、リポジトリなど既存アプリの中核処理 |
| `src/SupportCaseManager.Ai.Contracts` | AI回答支援用DTO |
| `src/SupportCaseManager.Ai.Core` | RAG、検索、Fact解決、プロンプト、LLM、ドラフト保存、診断ログ |
| `src/SupportCaseManager.AiAssistant.App` | 独立AI回答支援WPFアプリ |
| `tests/*` | xUnitテスト |

---

## 動作要件

- Windows 10 / 11
- .NET 10 SDK
- AI回答支援でOllamaを使う場合は、ローカルOllamaと対象モデル

---

## 起動

### 既存サポート案件管理アプリ

```powershell
dotnet run --project src\SupportCaseManager.App\SupportCaseManager.App.csproj
```

### AI回答支援アプリ

```powershell
dotnet run --project src\SupportCaseManager.AiAssistant.App\SupportCaseManager.AiAssistant.App.csproj
```

---

## ビルド / テスト

```powershell
dotnet build SupportCaseManager.slnx
dotnet test SupportCaseManager.slnx
dotnet build src\SupportCaseManager.AiAssistant.App\SupportCaseManager.AiAssistant.App.csproj
```

配布用ビルド:

```powershell
dotnet publish src\SupportCaseManager.App\SupportCaseManager.App.csproj -p:PublishProfile=WinX64SingleFile
```

---

## 既存サポート案件管理アプリ

主な機能:

- 案件メタ情報管理: 受付日、会社名、サポート番号、ステータス、保存先フォルダ
- 案件フォルダ作成: `YYYYMMDD(会社名_サポート番号)ステータス_YYYYMMDD` 形式
- 履歴/検索: `cases-index.json` と実ディレクトリを横断
- ノート編集: お客様ご相談内容、調査内容、回答内容などのテキスト追記
- 旧フォルダ互換: 既存フォルダ名からメタ情報を復元
- テンプレート、サブフォルダ作成、ショートカット、ダークモード

既存アプリの設定/ログ:

| パス | 説明 |
| --- | --- |
| `config/user-settings.json` | ベースフォルダ、ステータス、履歴、テンプレートなど |
| `%LOCALAPPDATA%\itoke\SupportCaseManager\user-settings.json` | `config/` が無い場合の設定保存先 |
| `<ベースフォルダ>/cases-index.json` | 案件検索用インデックス |
| `logs/SupportCaseManager.log` | 既存アプリの実行ログ |
| `<案件フォルダ>/*.txt` | 既存ノート |

---

## AI回答支援アプリ

目的:

- 過去案件、ローカルマニュアル、公式ドキュメントを根拠として回答案を作成
- 生成結果に根拠、参照元、要確認事項、信頼度、警告を表示
- 既存案件フォルダや既存ノートには自動書き込みしない

主な機能:

- 設定: `ai-data` / `ai-index`、Ollama、モデル、プロンプト上限、根拠件数
- インデックス: 過去案件、TXT/MDマニュアル、公式ドキュメント
- 検索: 過去案件、Manual、OfficialDocの統合検索
- 根拠制御: SourceTypeフィルタ、選択/除外、最大根拠件数制御
- 回答生成: Fake / Ollama切り替え、Ollama `/api/chat`
- Fact解決: `CuratedFactCatalog` を優先して最新バージョンなどの重要Factを確定
- 保存: AIドラフトを `ai-data/drafts` に保存
- 診断: AI専用ログを `ai-data/logs/AiAssistant.log` に保存

AI / RAG 構成:

- AI回答支援は local-first のローカルOllamaを利用可能
- 既定の local LLM model は `qwen3:14b`
- Ollama AI client は `http://localhost:11434/api/chat` を呼び出し、`message.content` を回答本文として取り出す
- Thinking は既定で無効化し、対応モデルには `think: false` を送信
- RAGにより `PastCaseNote` / `Manual` / `OfficialDoc` / `CuratedFactCatalog` から根拠を検索
- 画面で選択した根拠のみをLLMへ渡す
- `ai-index/products/<製品名>/` は製品別のRAG検索用AI index
- プロンプトは `src/SupportCaseManager.Ai.Core/Prompts/` のMarkdownファイルで管理
- 生成物は `ai-data/drafts/` の generated draft として保存し、人間が確認してから利用

AI-BOM / AI Supply Chain 観点のAI関連資産:

- local LLM model: `qwen3:14b`
- Ollama AI client: `src/SupportCaseManager.Ai.Core/Llm/OllamaClient.cs`
- RAG index: `ai-index/products/<製品名>/`
- prompts: `src/SupportCaseManager.Ai.Core/Prompts/support-answer-*.md`
- evidence data sources: `PastCaseNote`, `Manual`, `OfficialDoc`, `CuratedFactCatalog`
- generated drafts: `ai-data/drafts/`
- AI利用台帳: `config/ai-inventory.json`
- AI利用説明: `docs/AI_USAGE.md`

プライバシー方針:

- local-first
- cloud LLM disabled by default
- human review required
- no automatic customer send
- no automatic note write-back

AI用データ:

| パス | 説明 |
| --- | --- |
| `ai-data/settings.json` | AI回答支援アプリ専用設定 |
| `ai-data/drafts/` | 生成した回答案のJSON保存先 |
| `ai-data/logs/AiAssistant.log` | AI回答支援専用ログ |
| `ai-index/products/<製品名>/` | 製品別インデックス |
| `ai-index/products/<製品名>/curated-facts.json` | 製品別の正本Fact |

---

## CuratedFactCatalog

最新バージョンなど、サポート側で正本管理すべきFactはRAG抽出だけで断定しません。

例:

```json
{
  "productName": "Checkmarx",
  "latestSastVersion": "9.7.0",
  "latestEnginePackVersion": "9.7.6",
  "latestHotfixVersion": "HF10",
  "sourceUrls": [
    "https://docs.checkmarx.com/en/34965-321884-release-notes-for-9-7-0.html",
    "https://docs.checkmarx.com/en/34965-591177-engine-pack-version-9-7-6.html",
    "https://docs.checkmarx.com/en/34965-337700-hotfixes-9-7-0.html"
  ],
  "updatedAt": "2026-06-10T00:00:00+09:00"
}
```

優先順位:

1. `CuratedFactCatalog`
2. `UserConfirmed` 候補
3. 組み込みCurated fallback
4. `ProductFactCatalog` / `VersionCatalog`
5. `OfficialDoc` 候補
6. `Manual`
7. `PastCaseNote`

---

## 安全設計

- 既存ノートへ自動追記しない
- `cases-index.json` / `user-settings.json` をAI側から変更しない
- 自動送信、自動返信、自動クローズはしない
- APIキー、メールアドレス、電話番号などはログ出力前にマスクする
- クラウドLLM利用時はマスキング前提。初期利用はローカルOllama想定
- LLM出力は必ず人間が確認する

---

## 生成物の扱い

以下はビルド/検証で生成される一時物です。リポジトリには含めません。

- `publish/`
- `_publish/`
- `_verify_build/`
- `**/_verify_build/`
- `bin/`
- `obj/`
- `logs/`
- `docs/SupportCaseManager_UserGuide_ja_updated*.docx`
