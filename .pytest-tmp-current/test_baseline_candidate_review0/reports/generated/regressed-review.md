# RAG regression comparison: regressed-review

Synthetic/offline comparison. Source paths and document text are omitted.

- Status: failed
- Baseline: reference.json
- Candidate: candidate.json
- Input fingerprints match: True
- Regressed configurations: 1
- Missing configurations: 0
- Improved configurations: 0
- Unchanged configurations: 0
- New configurations: 0

Performance timing deltas are informational and do not affect status.

| status | chunk_strategy | search_method | reranker | filter_mode | top_k | findings |
| --- | --- | --- | --- | --- | --- | --- |
| regressed | fixed | bm25 | none | product_and_version | 3 | mrr dropped by 0.500000 |
