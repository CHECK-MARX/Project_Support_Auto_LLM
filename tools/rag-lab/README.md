# SupportCaseManager RAG Lab

`rag-lab` is an offline evaluation environment for comparing RAG behavior without
changing the SupportCaseManager WPF application or its production indexes.

## Implemented scope (Phases 2 and 3)

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
- JSON, CSV, and Markdown comparison reports

Embeddings, reranking, and Codex evidence JSON are not implemented yet. They belong
to Phase 4. This package is not referenced by any C# project and is not part of the
WPF runtime path.

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
metadata retention, keyword/BM25 ranking, product/version filters, evaluation
metrics, stable chunk IDs, invalid JSON, read-only source handling, path traversal
rejection, and output-root restrictions.

## Configuration

Copy `config.example.json` only when a local evaluation configuration is needed.
Relative paths are resolved from the `rag-lab` directory. Do not configure
`outputRoot` outside `reports/generated/` when adding the Phase 3 runner.

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
- `samples/evaluation_cases.json`: synthetic expected-result definitions reserved
  for the Phase 3 evaluator

## Evaluation

Run the complete synthetic comparison from `tools\rag-lab`:

```powershell
python run_rag_lab.py evaluate
```

To select a different configuration or safe report name:

```powershell
python run_rag_lab.py evaluate --config config.example.json --report-name local-comparison
```

The evaluation compares both search methods, all configured chunking strategies,
metadata filter modes, and Top-K values. Reports are written only to:

```text
tools/rag-lab/reports/generated/
```

Open `phase3-comparison.md` for the compact comparison table, CSV for spreadsheet
analysis, or JSON for query-level metrics and redacted result details. Generated
reports are local artifacts and are not committed.
