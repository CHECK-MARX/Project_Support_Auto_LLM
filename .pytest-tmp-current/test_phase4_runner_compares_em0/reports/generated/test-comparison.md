# RAG evaluation: test-comparison

Synthetic/offline evaluation. No source absolute paths are included.

| chunk_strategy | search_method | reranker | filter_mode | top_k | mean_precision_at_k | mean_recall_at_k | mrr | mean_ndcg_at_k | mean_search_time_ms | mean_required_term_coverage_at_k | excluded_term_hit_count | product_confusion_count | version_mismatch_count | quality_gate_passed |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| heading | hash_embedding | none | product_and_version | 1 | 1.000000 | 1.000000 | 1.000000 | 1.000000 | 0.364300 | 1.000000 | 0 | 0 | 0 | True |
| heading | hash_embedding | lexical | product_and_version | 1 | 1.000000 | 1.000000 | 1.000000 | 1.000000 | 0.292300 | 1.000000 | 0 | 0 | 0 | True |
| heading | hybrid | none | product_and_version | 1 | 1.000000 | 1.000000 | 1.000000 | 1.000000 | 0.218300 | 1.000000 | 0 | 0 | 0 | True |
| heading | hybrid | lexical | product_and_version | 1 | 1.000000 | 1.000000 | 1.000000 | 1.000000 | 0.374800 | 1.000000 | 0 | 0 | 0 | True |
