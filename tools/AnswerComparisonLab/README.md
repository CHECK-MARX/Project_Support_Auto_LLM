# Answer Comparison Lab

This local tool compares the existing deterministic answer with polishing against
the grounded candidate on the same retrieved evidence. It reads the private
60-case review set from `rag-lab` and processes only the 40 development cases.
The 20 holdout cases are never selected by this tool.

Supply five paths: the candidate JSON, the saved AI settings JSON, the QAC
closed-case root, the Checkmarx closed-case root, and a private output JSON under
`tools/rag-lab/reports/generated/`. Add `--dry-run` to validate retrieval and
same-case exclusion without calling the answer model. Use `--start` and `--limit`
to process a bounded development range. `--compact`, `--evidence-count`,
`--output-tokens`, `--context-tokens`, and `--timeout-seconds` are diagnostic
settings and are recorded in the output for comparisons between runs. Timeout
overrides apply only to this lab process; the application's 30/60-second
polishing cap is unchanged.
For a three-product review set, pass `--klocwork-root` with the Klocwork closed-case
folder. The local source archive is read only.

For a preliminary Shadow Gold Pilot, pass `--case-ids` with exactly one fixed,
ready development case per product and `--shadow-status` with the status JSON
from `finalize_gold_pilot.py`. This mode rejects holdout IDs and a status for a
different candidate set. `--review-output` optionally stores answer text in a
private file under `rag-lab/reports/generated/`; the metrics report remains
text-free. Shadow results do not constitute Human Approved or formal quality
improvement certification.

The lab applies the qwen3:8b Boolean `think=false` capability profile in
memory. The saved provider profile can belong to an embedding-only model and
must not be carried over to the comparison model.
The grounded candidate supplies a JSON schema to the local Ollama request;
the baseline polishing route retains its existing request format. A response
ending at the output-token limit is reported separately from malformed JSON.

The tool never writes to the source case folders, saved settings, or production
indexes. It uses `qwen3:8b` through loopback Ollama and writes only per-case
metrics, status, phase timings, generic rejection reasons, and model error
categories. It omits question, answer, evidence text, exception messages, and
source paths from the report.

For isolated source-coverage E2E runs, `--index-folder` accepts an existing
index copy under `tools/rag-lab/reports/generated/`. The production index path
in saved settings is overridden only in this lab process. The official source
coverage helper in `tools/SourceCoverageLab` builds selected vendor URLs through
`AiOfficialDocumentIndexBuilder` and merges their chunks into such an isolated
copy; it never writes to the saved index or source documents. Keep the previous
ranked E2E and fixed-evidence regression reports separate from coverage runs.

These runs are provisional. Historical reply drafts are not human-confirmed
ground truth, and the candidate set still requires privacy review. A status of
`ReadyForHumanReview` means the automated checks passed; it does not mean a
customer-ready answer or a measured quality improvement.
