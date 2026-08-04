# Reports

Phases 3 through 5 write generated JSON, CSV, and Markdown comparison reports and
Codex evidence JSON under `reports/generated/`. Phase 5 reports include the
quality-gate result, recommended configuration, required/excluded-term metrics,
and redacted input fingerprints. That directory is intentionally excluded from
Git.

No report writer may write outside `reports/generated/`.
