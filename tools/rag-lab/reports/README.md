# Reports

Phases 3 through 7 write generated JSON, CSV, and Markdown comparison reports,
regression comparisons, and Codex evidence JSON under `reports/generated/`.
Phase 5 reports include the
quality-gate result, recommended configuration, required/excluded-term metrics,
and redacted input fingerprints. That directory is intentionally excluded from
Git.

Phase 7 regression reports compare two schema-version 3 or newer synthetic
evaluation reports. They contain configuration identities, metric deltas, and
file names only; source paths and document text are not copied.

No report writer may write outside `reports/generated/`.
