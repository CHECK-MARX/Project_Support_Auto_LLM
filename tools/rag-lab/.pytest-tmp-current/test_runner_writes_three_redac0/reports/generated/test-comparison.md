# RAG evaluation: test-comparison

Synthetic/offline evaluation. No source absolute paths are included.

| chunk_strategy | search_method | reranker | filter_mode | top_k | mean_precision_at_k | mean_recall_at_k | mrr | mean_ndcg_at_k | mean_search_time_ms | mean_required_term_coverage_at_k | excluded_term_hit_count | product_confusion_count | version_mismatch_count | quality_gate_passed |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| fixed | keyword | none | none | 1 | 1.000000 | 1.000000 | 1.000000 | 1.000000 | 0.038000 | 1.000000 | 0 | 0 | 0 | True |
| fixed | keyword | none | none | 3 | 0.333333 | 1.000000 | 1.000000 | 1.000000 | 0.038000 | 1.000000 | 0 | 0 | 0 | True |
| fixed | keyword | none | product_and_version | 1 | 1.000000 | 1.000000 | 1.000000 | 1.000000 | 0.026500 | 1.000000 | 0 | 0 | 0 | True |
| fixed | keyword | none | product_and_version | 3 | 0.333333 | 1.000000 | 1.000000 | 1.000000 | 0.026500 | 1.000000 | 0 | 0 | 0 | True |
| fixed | bm25 | none | none | 1 | 1.000000 | 1.000000 | 1.000000 | 1.000000 | 0.019300 | 1.000000 | 0 | 0 | 0 | True |
| fixed | bm25 | none | none | 3 | 0.333333 | 1.000000 | 1.000000 | 1.000000 | 0.019300 | 1.000000 | 0 | 0 | 0 | True |
| fixed | bm25 | none | product_and_version | 1 | 1.000000 | 1.000000 | 1.000000 | 1.000000 | 0.018500 | 1.000000 | 0 | 0 | 0 | True |
| fixed | bm25 | none | product_and_version | 3 | 0.333333 | 1.000000 | 1.000000 | 1.000000 | 0.018500 | 1.000000 | 0 | 0 | 0 | True |
| structured | keyword | none | none | 1 | 1.000000 | 1.000000 | 1.000000 | 1.000000 | 0.029300 | 1.000000 | 0 | 0 | 0 | True |
| structured | keyword | none | none | 3 | 0.333333 | 1.000000 | 1.000000 | 1.000000 | 0.029300 | 1.000000 | 0 | 0 | 0 | True |
| structured | keyword | none | product_and_version | 1 | 1.000000 | 1.000000 | 1.000000 | 1.000000 | 0.038100 | 1.000000 | 0 | 0 | 0 | True |
| structured | keyword | none | product_and_version | 3 | 0.333333 | 1.000000 | 1.000000 | 1.000000 | 0.038100 | 1.000000 | 0 | 0 | 0 | True |
| structured | bm25 | none | none | 1 | 1.000000 | 1.000000 | 1.000000 | 1.000000 | 0.020700 | 1.000000 | 0 | 0 | 0 | True |
| structured | bm25 | none | none | 3 | 0.333333 | 1.000000 | 1.000000 | 1.000000 | 0.020700 | 1.000000 | 0 | 0 | 0 | True |
| structured | bm25 | none | product_and_version | 1 | 1.000000 | 1.000000 | 1.000000 | 1.000000 | 0.020600 | 1.000000 | 0 | 0 | 0 | True |
| structured | bm25 | none | product_and_version | 3 | 0.333333 | 1.000000 | 1.000000 | 1.000000 | 0.020600 | 1.000000 | 0 | 0 | 0 | True |
