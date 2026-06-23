# AI Usage / AI-BOM Notes

This repository contains an AI answer assistance feature for the `SupportCaseManager` .NET WPF desktop solution. The AI feature is implemented for support answer generation, evidence retrieval, generated draft review, and RAG-based response assistance.

This document is for transparency, manual review, AI-BOM preparation, and AI Supply Chain review. It documents the actual AI model, AI client, local LLM provider, prompt assets, RAG index, evidence data sources, and generated artifacts used by the application. It is not a dummy implementation and does not describe unused AI SDKs.

## AI Provider and AI Client

- AI provider: Ollama
- AI client implementation: `src/SupportCaseManager.Ai.Core/Llm/OllamaClient.cs`
- Local LLM endpoint: `http://localhost:11434/api/chat`
- Default AI model: `qwen3:14b`
- Thinking default: disabled (`think: false` when the application setting disables thinking)
- Default deployment: local LLM on the user's machine
- Cloud LLM transmission: disabled by default

The Ollama AI client sends prompts to `/api/chat` using UTF-8 JSON. The HTTP response is handled as an Ollama chat response first; the application extracts `message.content` before parsing the support answer JSON. The full Ollama response is not treated directly as the answer DTO.

When thinking is disabled, the client sends `think: false`. For qwen3 models, the prompt also receives a `/no_think` prefix. If a model such as `gpt-oss:20b` returns only `message.thinking` and leaves `message.content` empty, the client retries once with a stronger no-think instruction. For `gpt-oss:*` retry requests, the output token budget is raised to at least `800` to avoid exhausting the response on thinking output only. If the retry still returns no `message.content`, answer generation fails safely instead of treating thinking text as a customer reply.

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

This is the AI index used for RAG evidence retrieval. It currently represents local searchable indexes for support cases, manuals, official documents, and curated fact catalogs. It is not documented as a vector database unless a real vector database implementation is added.

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

- `config/ai-inventory.json`
- `docs/AI_USAGE.md`
- prompt files under `src/SupportCaseManager.Ai.Core/Prompts/`
- Ollama AI client implementation under `src/SupportCaseManager.Ai.Core/Llm/`
- RAG index path `ai-index/products/<productName>/`
- generated draft path `ai-data/drafts/`

AI assets managed by this repository include:

- Ollama local LLM provider
- `qwen3:14b` default AI model
- optional locally configured Ollama models, such as `gpt-oss:20b`, when selected by the user
- RAG prompts
- RAG search index / AI index
- evidence data sources
- generated support answer drafts
- human review workflow

If Checkmarx AISC or another AI Supply Chain tool does not automatically detect all AI assets, this inventory and documentation provide the manual review basis. The purpose is accurate inventory and auditability of actual AI usage, not synthetic detection.
