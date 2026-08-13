# Synthetic quality baselines

This directory contains compact, reviewed baselines for the artificial sample
dataset only. Baselines contain configuration identities, aggregate quality
metrics, and input fingerprints. They must not contain document text, customer
data, absolute paths, or performance timing values.

Update a baseline only after reviewing an intentional retrieval-quality change.
The RAG Lab treats these files as read-only inputs and writes all generated
candidate and regression reports below `reports/generated/`.

Validate a reviewed baseline before committing it:

```powershell
python run_rag_lab.py validate-baseline --baseline baselines/phase8-reference.json
```

Regression comparison applies the same strict validation automatically whenever
its baseline is loaded from this directory.

Generate a review candidate from a passing report without writing here:

```powershell
python run_rag_lab.py baseline-candidate --source ci-candidate.json --output-name next-reference
```

The candidate is created below `reports/generated/`. Moving it into this directory
is a separate, deliberate review action.

Review that compact candidate against the tracked reference first:

```powershell
python run_rag_lab.py review-baseline --baseline baselines/phase8-reference.json --candidate next-reference.json --output-name next-reference-review
```

The review is strict and never writes into this directory.

Before manual promotion, independently generate the candidate twice and run
`check-reproducibility`, then run `baseline-readiness`. A `ready` result is a review
input, not permission for the tool to modify this directory automatically.
