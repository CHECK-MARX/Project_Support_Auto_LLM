# AI Bill of Materials

This document is the human-readable AI-BOM for the SupportCaseManager AI Assistant. It complements:

- `config/aibom.cdx.json`
- `config/ai-inventory.json`
- `docs/AI_USAGE.md`

It is intended for manual AI Supply Chain review, AI asset inventory review, and audit evidence. It does not guarantee automatic ingestion by Checkmarx One AI Supply Chain Global Inventory.

## Application

| Field | Value |
| --- | --- |
| Application | SupportCaseManager AI Assistant |
| Type | .NET 10 / WPF desktop application |
| Entry project | `src/SupportCaseManager.AiAssistant.App/SupportCaseManager.AiAssistant.App.csproj` |
| AI core project | `src/SupportCaseManager.Ai.Core/SupportCaseManager.Ai.Core.csproj` |
| AI contracts project | `src/SupportCaseManager.Ai.Contracts/SupportCaseManager.Ai.Contracts.csproj` |
| Default deployment | Local desktop |
| Human review | Required |
| Automatic customer email | Not implemented |
| Automatic reply | Not implemented |
| Automatic case close | Not implemented |

## AI Runtime and Endpoint

| Asset | Type | Provider | Endpoint | Deployment | Notes |
| --- | --- | --- | --- | --- | --- |
| Ollama | Local LLM runtime | Ollama | `http://localhost:11434/api/chat` | Local | Called directly via `HttpClient` |

Implementation:

- `src/SupportCaseManager.Ai.Core/Llm/OllamaClient.cs`
- `src/SupportCaseManager.Ai.Core/Llm/OllamaConnectionChecker.cs`
- `src/SupportCaseManager.Ai.Core/Llm/OllamaRequestBuilder.cs`

The application sends UTF-8 JSON to Ollama `/api/chat`, then extracts `message.content` from the Ollama response before parsing the support answer JSON.

## AI Models

| Model | Provider | Deployment | Required | Usage | Notes |
| --- | --- | --- | --- | --- | --- |
| `qwen3:14b` | Ollama | Local | Default | Support answer generation | Default configured chat model |
| `gpt-oss:20b` | Ollama | Local | Optional | Support answer generation | User-selectable local model |

Thinking controls:

- The application sends `think: false` when thinking is disabled.
- For qwen3 models, `/no_think` is also prefixed to prompts.
- If `gpt-oss:*` returns only `message.thinking` and no `message.content`, the client retries once with stronger no-think instructions.
- For `gpt-oss:*` retry requests, `num_predict` is raised to at least `800`.
- If the retry still returns no `message.content`, generation fails safely and the thinking text is not treated as a customer answer.

## Prompts

| Prompt | Path | Purpose |
| --- | --- | --- |
| Support answer system prompt | `src/SupportCaseManager.Ai.Core/Prompts/support-answer-system-prompt.md` | RAG-only support answer behavior and safety instructions |
| Support answer output prompt | `src/SupportCaseManager.Ai.Core/Prompts/support-answer-output-prompt.md` | Required JSON output shape and evidence rules |

Prompt rules include:

- Use only provided case context, notes, facts, and RAG evidence.
- Do not assert unsupported facts.
- Separate customer reply and internal memo.
- Do not include internal paths, evidence IDs, case numbers, past customer names, or internal memo in the customer reply.
- Require evidence, required checks, confidence, and warnings.
- Require human review.
- Do not assume automatic sending, automatic reply, or automatic close.

## RAG Evidence and Data Sources

| Source | Priority | Customer-visible | Notes |
| --- | ---: | --- | --- |
| `CuratedFactCatalog` | 1 | Yes, when confirmed | Support-owned canonical facts, such as latest versions |
| `OfficialDoc` | 2 | Yes | Preferred for freshness-sensitive answers |
| `Manual` | 3 | Yes | Local TXT/MD/PDF/DOCX/XLSX/PPTX/HTML/CSV/TSV manual evidence |
| `PastCaseNote` | 4 | No | Historical support evidence only; can contain customer or case-specific content |

Index path:

```text
ai-index/products/<productName>/
```

Curated fact path example:

```text
ai-index/products/<productName>/curated-facts.json
```

TopN fallback:

- Normal evidence selection uses the user's selected evidence.
- If no evidence is selected and `TopN fallback` is enabled, the application may send top-scored evidence up to the configured maximum.
- The UI reports these items as `LLM送信予定` / evidence to send.
- Fallback evidence still requires human review.

## Generated AI Artifacts

| Artifact | Path | Notes |
| --- | --- | --- |
| Generated answer drafts | `ai-data/drafts/` | AI-only draft storage; existing case notes are not modified |
| Diagnostic log | `ai-data/logs/AiAssistant.log` | AI assistant diagnostic log |
| Settings | `ai-data/settings.json` | AI assistant settings; existing `user-settings.json` is not modified |

## Safety and Privacy Controls

| Control | Status |
| --- | --- |
| Local-first LLM default | Enabled |
| Cloud LLM default | Disabled |
| Existing notes write-back | Disabled |
| Automatic customer email | Disabled |
| Automatic reply | Disabled |
| Automatic close | Disabled |
| Human review | Required |
| API key logging | Avoided / masked |
| Email/phone logging | Masked where diagnostic logging is used |
| Customer reply internal path removal | Enabled |
| Customer reply evidence ID removal | Enabled |
| Customer reply past-case customer/case fragment removal | Enabled |

Past case safety:

- `PastCaseNote` may contain customer names, company names, support numbers, and historical email text.
- `PastCaseNote` is not customer-visible evidence.
- If a fallback answer can only rely on `PastCaseNote`, the customer reply does not list past case titles or excerpts.
- The safe response asks the support engineer to confirm product manuals or official documents before answering.

## Known Automatic Detection Limitations

Checkmarx One AI Supply Chain may not automatically show this project in Global Inventory if detection relies on AI SDKs, common AI frameworks, model files, or package manifests.

Reasons:

- The application uses C# `HttpClient` to call local Ollama directly.
- It does not reference OpenAI, Anthropic, LangChain, Semantic Kernel, or similar AI SDK NuGet packages.
- The models are configured as local Ollama model names, not bundled model files.
- RAG indexes are local application data, not a standard vector database dependency.

For review, use:

- `config/aibom.cdx.json`
- `config/ai-inventory.json`
- `docs/AI_USAGE.md`
- `docs/AI_BOM.md`

## Review Checklist

- [ ] Confirm Checkmarx scan targets the `main` branch of `CHECK-MARX/Project_Support_Auto_LLM`.
- [ ] Confirm the scan root includes `Project_Support_c#`.
- [ ] Confirm AI Supply Chain / AI-BOM scanning is enabled for the project.
- [ ] Confirm the latest scan date is after the AI-BOM files were committed.
- [ ] If the Global Inventory still does not detect the app automatically, attach `config/aibom.cdx.json` and `docs/AI_BOM.md` as manual evidence.
