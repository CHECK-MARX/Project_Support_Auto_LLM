# Reports

Phases 3 through 13 write generated JSON, CSV, and Markdown comparison reports,
regression comparisons, and Codex evidence JSON under `reports/generated/`.
Phase 5 reports include the
quality-gate result, recommended configuration, required/excluded-term metrics,
and redacted input fingerprints. That directory is intentionally excluded from
Git.

Phase 7 regression reports compare two schema-version 3 or newer synthetic
evaluation reports. They contain configuration identities, metric deltas, and
file names only; source paths and document text are not copied.

Phase 8 adds a compact reviewed baseline under `../baselines/`. It is read-only at
runtime; candidate and regression outputs remain in this generated directory.

Phase 10 creates strict, review-only baseline candidates here from a passing
evaluation report. It does not copy candidates into `../baselines/`.

Phase 11 writes strict candidate-review JSON and Markdown here after validating
both the tracked baseline and generated compact candidate.

Phase 12 writes candidate reproducibility reports with canonical digests. Phase 13
writes a deterministic readiness result that combines regression and independent
reproduction checks. Neither phase modifies `../baselines/`.

No report writer may write outside `reports/generated/`.
