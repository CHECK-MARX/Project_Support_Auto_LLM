# SupportCaseManager RAG Lab

`rag-lab` is an offline evaluation environment for comparing RAG behavior without
changing the SupportCaseManager WPF application or its production indexes.

## Implemented scope (Phases 2 through 5)

Implemented in this phase:

- UTF-8 JSON input with synthetic sample data
- Unicode and newline normalization
- Configurable separation of greetings, signatures, webinar notices, quoted mail,
  separators, and generated headers
- Metadata that preserves product, support, version, platform, source, question,
  cause, resolution, and reply fields without filling unknown values
- Fixed-length, paragraph, heading, and structured-field chunking
- Canonical path checks for all input and generated-output paths
- Unit tests using synthetic data only
- Offline keyword and BM25 search
- Exact product and target-version metadata filters
- Precision@K, Recall@K, MRR, nDCG@K, correct-document rank, timing,
  product-confusion, and version-mismatch measurements
- Required-term coverage and excluded-term hit measurements
- JSON, CSV, and Markdown comparison reports
- Replaceable `EmbeddingProvider` and `Reranker` protocols
- Deterministic offline token-hash vector baseline
- BM25 plus vector retrieval with reciprocal-rank fusion
- Optional offline lexical reranking
- Top 1-5 Codex evidence JSON with match reasons and warnings
- Configurable quality gates with a deterministic recommended configuration
- SHA-256 input fingerprints for repeatable evaluation runs
- A `verify` command suitable for a local quality check or CI job

The token-hash vector baseline verifies the embedding integration path but is not a
semantic language model. No model is downloaded. A local semantic model can be
added later by implementing `EmbeddingProvider`. This package is not referenced by
any C# project and is not part of the WPF runtime path.

## Safety boundary

- The tool has no network client and requires no API key.
- Input is accepted only from explicitly configured roots and is opened read-only.
- Generated output is accepted only below `reports/generated/`.
- Public serialization omits original absolute paths and emits only the source file
  name.
- Production files, production indexes, and case folders are never written.
- `.cache/`, `models/`, `.venv/`, and `reports/generated/` are excluded from Git.

## Windows setup

Run these commands from `tools\rag-lab` in PowerShell:

```powershell
py -3.13 -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
python -m pip install -r requirements.txt
```

The runtime uses only the Python standard library. `pytest` is the sole test
dependency.

## Unit tests

```powershell
python -m pytest
```

The tests cover Japanese UTF-8 input, normalization, all four chunking strategies,
metadata retention, keyword/BM25/vector/hybrid ranking, replaceable providers,
reranking, product/version filters, evaluation metrics, Codex evidence generation,
stable chunk IDs, invalid JSON, read-only source handling, path traversal rejection,
output-root restrictions, required/excluded terms, quality-gate decisions,
recommended-configuration selection, and input fingerprints.

## Configuration

Copy `config.example.json` only when a local evaluation configuration is needed.
Relative paths are resolved from the `rag-lab` directory. Do not configure
`outputRoot` outside `reports/generated/`.

Normalization categories are:

- `greeting`
- `signature`
- `webinar`
- `quoted_mail`
- `separator`
- `generated_header`

Excluded content is returned as separate segments. The source text is never edited.

Chunking strategy values are `fixed`, `paragraph`, `heading`, and `structured`.
The `structured` strategy uses only explicitly supplied question, cause, resolution,
manufacturer reply, and customer final reply fields, or matching headings in text.
It does not infer missing facts.

## Samples

- `samples/documents.json`: synthetic documents with no real customer information
- `samples/evaluation_cases.json`: synthetic expected-result definitions for the
  comparison evaluator and evidence generator

## Evaluation

Run the complete synthetic comparison from `tools\rag-lab`:

```powershell
python run_rag_lab.py evaluate
```

To select a different configuration or safe report name:

```powershell
python run_rag_lab.py evaluate --config config.example.json --report-name local-comparison
```

The evaluation compares keyword, BM25, offline hash-vector, and hybrid search;
configured rerankers; all configured chunking strategies; metadata filter modes;
and Top-K values. Reports are written only to:

```text
tools/rag-lab/reports/generated/
```

Open `phase5-comparison.md` for the compact comparison table, CSV for spreadsheet
analysis, or JSON for query-level metrics and redacted result details. Generated
reports are local artifacts and are not committed.

Each JSON report records the configured quality thresholds, pass/fail result,
violations, the recommended passing configuration for the preferred Top-K, and
SHA-256 fingerprints of the synthetic input files. Fingerprints contain only the
file name, byte size, and digest; absolute paths and source content are omitted.

Run the quality gate as a command-line verification step:

```powershell
python run_rag_lab.py verify
```

`verify` performs the complete comparison, prints the gate result and recommended
configuration, and returns exit code `0` when at least one preferred-Top-K
configuration passes. It returns exit code `1` when the gate fails. Thresholds and
`preferredTopK` are configured in the `qualityGate` object in
`config.example.json`.

The included thresholds and results apply only to synthetic sample data. Passing
the gate is a regression signal for this lab and is not proof of production RAG
quality.

## Codex evidence JSON

Generate a future Codex-input payload for a synthetic evaluation query:

```powershell
python run_rag_lab.py evidence --query-id q001
```

The default uses heading chunks, hybrid retrieval, lexical reranking, product and
version filters, and at most three evidence documents. `--top-k` accepts 1 through
5. The output contains `query` and `selectedEvidence`, including selection reason,
product/version matches, keyword matches, source type, stale/conflict warnings, and
unverified fields. It does not contain the source absolute path.

This JSON is preparation for future integration only. It is not sent to Codex and
does not alter the current App Server or WPF behavior.

## Extension interfaces

- Implement `EmbeddingProvider.embed()` to connect another local embedding model.
- Implement `Reranker.rerank()` to connect another local reranker.
- Keep implementations offline unless a separately reviewed policy explicitly
  permits another execution model.
