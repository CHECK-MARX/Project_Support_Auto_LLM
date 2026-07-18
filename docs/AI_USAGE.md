# AI Usage / AI-BOM Notes

This repository contains an AI answer assistance feature for the `SupportCaseManager` .NET WPF desktop solution. The AI feature is implemented for support answer generation, evidence retrieval, generated draft review, and RAG-based response assistance.

This document is for transparency, manual review, AI-BOM preparation, and AI Supply Chain review. It documents the actual AI model, AI client, local LLM provider, prompt assets, RAG index, evidence data sources, and generated artifacts used by the application. It is not a dummy implementation and does not describe unused AI SDKs.

## AI Provider and AI Client

- AI provider: Ollama
- AI client implementation: `src/SupportCaseManager.Ai.Core/Llm/OllamaClient.cs`
- Embedding client implementation: `src/SupportCaseManager.Ai.Core/Llm/OllamaEmbeddingClient.cs`
- Local LLM endpoints: `http://localhost:11434/api/chat` and `http://localhost:11434/api/embed`
- Quality presets: Fast `qwen3:8b`, Standard `gemma4:26b`, Quality `gemma4:31b`
- Backward-compatible custom default: `qwen3:14b` for settings created by earlier versions
- Custom model: any model already present in Ollama; the application never runs `ollama pull`
- Thinking and structured-output parameters: selected from `ModelCapabilityProfile` per model
- Default deployment: local LLM on the user's machine
- Cloud LLM transmission: disabled by default

The Ollama AI client sends prompts to `/api/chat` using UTF-8 JSON. The HTTP response is handled as an Ollama chat response first; the application extracts `message.content` before parsing the support answer JSON. The full Ollama response is not treated directly as the answer DTO.

The client does not assume that every model supports `think` or JSON mode. Gemma 4 and GPT-OSS profiles omit unsupported thinking and format parameters and use plain-text output. A legacy Qwen configuration can still use `think: false` and `/no_think`. If Ollama returns HTTP 400 for an unsupported parameter, the client retries once without `think` and `format`; the compatibility test records the safe profile. If a response contains only `message.thinking`, the client retries once with a stronger content instruction and fails safely if `message.content` remains empty.

## RAG and Evidence Retrieval

The AI assistant uses RAG (retrieval augmented generation). It performs evidence retrieval from local data, lets the user select evidence, and sends the planned evidence set to the local LLM prompt.

Normally, the planned evidence set is built from user-selected evidence. If no evidence is selected and `TopN fallback` is enabled, the assistant may send the top-scored evidence items up to the configured maximum evidence count. The UI reports these items as `LLM送信予定` / evidence to send. This fallback is intended for testing and convenience; a support engineer must still review the evidence before using any draft.

RAG data sources:

1. `CuratedFactCatalog`
2. `OfficialDoc`
3. `Manual`
4. `PastCaseNote`

`CuratedFactCatalog` is used for support-owned canonical facts such as latest product versions. These curated facts are preferred over crawler or free-text extraction candidates.

`PastCaseNote` is historical support information and must not be used alone to assert current product specifications or latest versions. `Manual` and `OfficialDoc` provide local and official-document evidence. `OfficialDoc` is preferred for freshness-sensitive questions.

Past case content can contain customer names, support numbers, email fragments, and historical case-specific wording. These values are not customer-visible evidence and must not be copied into a customer reply draft.

## AI Index

The product-scoped AI index is stored under:

```text
ai-index/products/<productName>/
```

This is the AI index used for RAG evidence retrieval. Each product folder contains `knowledge-manifest.json` with source fingerprints, timestamps, chunk counts, schema/chunking version, and embedding model metadata. Existing indexes are usable immediately at startup. Local manuals and closed cases are checked in the background and only added, changed, or deleted files are reprocessed. Index files and settings are written through temporary files and replaced only after a successful write.

Search keeps the existing keyword retrieval and, when an embedding model is configured, calls the local Ollama `/api/embed` endpoint and combines cosine similarity with keyword rank using reciprocal-rank fusion. Vectors are stored in the product-scoped `embeddings-index.json`; only added or changed chunks are embedded, deleted chunks are removed, and unchanged vectors are reused. Source routing depends on the classified question type: current-version questions exclude past cases, while how-to and troubleshooting questions prefer manuals. When embedding is not configured or the endpoint fails, keyword plus local term-similarity ranking remains operational. No separate vector database is required.

Official documentation is not crawled on every startup. A successful official index is reused for seven days, after which the UI reports that an update is available. Explicit official-document updates can refresh it, and a failed refresh retains the previous index and fact catalogs.

## Prompt Assets

Prompt assets are part of the AI-BOM review surface:

- `src/SupportCaseManager.Ai.Core/Prompts/support-answer-system-prompt.md`
- `src/SupportCaseManager.Ai.Core/Prompts/support-answer-output-prompt.md`

The prompts instruct the local LLM to use RAG evidence only, avoid unsupported assertions, separate customer reply and internal memo, list required checks, include confidence and warnings, and require human review. The prompts also state no automatic sending.

## Generated Artifacts and Logs

Generated support answer drafts are saved under:

```text
ai-data/drafts/
```

AI diagnostic logs are written to:

```text
ai-data/logs/AiAssistant.log
```

Sensitive values such as API keys, phone numbers, and email addresses are masked before logging where diagnostic logging is used. The application avoids logging prompt content or generated content unnecessarily.

## Safety and Privacy Controls

- Local-first by default
- `cloudLlmDefault`: false
- Human review required
- No automatic customer email sending
- No automatic reply
- No automatic case close
- No automatic write-back to existing case notes
- Existing case data, existing notes, `cases-index.json`, and `user-settings.json` are not modified by AI answer generation
- Customer reply drafts are post-processed to remove internal paths, evidence IDs, past-case support numbers, company/contact names, email addresses, phone numbers, and common past-email fragments.
- If a fallback answer can only rely on `PastCaseNote` evidence, the customer reply does not list past-case titles or excerpts. It instead states that official documentation or product manuals must be checked before answering.

Generated drafts are assistance outputs. A support engineer must review the customer reply draft, internal memo, required checks, evidence, confidence, and warnings before using the result.

## AI-BOM / AI Supply Chain Review

For AI-BOM and AI Supply Chain review, use:

- `config/aibom.cdx.json`
- `config/ai-inventory.json`
- `docs/AI_BOM.md`
- `docs/AI_USAGE.md`
- prompt files under `src/SupportCaseManager.Ai.Core/Prompts/`
- Ollama AI client implementation under `src/SupportCaseManager.Ai.Core/Llm/`
- RAG index path `ai-index/products/<productName>/`
- generated draft path `ai-data/drafts/`

AI assets managed by this repository include:

- Ollama local LLM provider
- `qwen3:8b`, `gemma4:26b`, and `gemma4:31b` quality-preset models
- `qwen3:14b` backward-compatible custom/default model
- `gpt-oss:120b-cloud` optional cloud-quality profile
- optional locally installed Ollama models selected from `/api/tags`
- RAG prompts
- RAG search index / AI index
- evidence data sources
- generated support answer drafts
- human review workflow

If Checkmarx AISC or another AI Supply Chain tool does not automatically detect all AI assets, this inventory and documentation provide the manual review basis. The purpose is accurate inventory and auditability of actual AI usage, not synthetic detection.
