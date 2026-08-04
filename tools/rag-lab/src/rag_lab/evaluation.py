from __future__ import annotations

import math
import unicodedata
from dataclasses import asdict, dataclass
from statistics import fmean
from typing import Iterable

from .models import EvaluationCase
from .search import SearchResult


@dataclass(frozen=True, slots=True)
class QueryEvaluation:
    query_id: str
    top_k: int
    precision_at_k: float
    recall_at_k: float
    reciprocal_rank: float
    ndcg_at_k: float
    first_relevant_rank: int | None
    product_confusion_count: int
    version_mismatch_count: int
    returned_document_count: int
    search_time_ms: float

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


def _normalized(value: str | None) -> str | None:
    if value is None:
        return None
    return unicodedata.normalize("NFKC", value).strip().casefold()


def deduplicate_by_document(results: Iterable[SearchResult]) -> list[SearchResult]:
    selected: list[SearchResult] = []
    seen: set[str] = set()
    for result in results:
        if result.chunk.document_id in seen:
            continue
        seen.add(result.chunk.document_id)
        selected.append(result)
    return selected


def _is_relevant(result: SearchResult, case: EvaluationCase) -> bool:
    if case.expected_document_ids:
        return result.chunk.document_id in case.expected_document_ids
    if case.expected_support_ids:
        return result.chunk.metadata.support_id in case.expected_support_ids
    return False


def evaluate_results(
    case: EvaluationCase,
    results: Iterable[SearchResult],
    *,
    top_k: int,
    search_time_ms: float = 0.0,
) -> QueryEvaluation:
    if top_k <= 0:
        raise ValueError("top_k must be greater than zero")
    unique_results = deduplicate_by_document(results)
    relevant_flags = [_is_relevant(result, case) for result in unique_results]
    first_relevant_rank = next(
        (rank for rank, relevant in enumerate(relevant_flags, start=1) if relevant),
        None,
    )
    top_results = unique_results[:top_k]
    top_flags = relevant_flags[:top_k]
    relevant_count = sum(top_flags)
    expected_count = (
        len(set(case.expected_document_ids))
        if case.expected_document_ids
        else len(set(case.expected_support_ids))
    )
    precision = relevant_count / top_k
    recall = relevant_count / expected_count if expected_count else 0.0
    reciprocal_rank = 1.0 / first_relevant_rank if first_relevant_rank else 0.0
    dcg = sum(
        1.0 / math.log2(rank + 1)
        for rank, relevant in enumerate(top_flags, start=1)
        if relevant
    )
    ideal_relevant = min(expected_count, top_k)
    ideal_dcg = sum(
        1.0 / math.log2(rank + 1)
        for rank in range(1, ideal_relevant + 1)
    )
    ndcg = dcg / ideal_dcg if ideal_dcg else 0.0
    expected_product = _normalized(case.product)
    expected_version = _normalized(case.target_version)
    product_confusions = sum(
        1
        for result in top_results
        if expected_product is not None
        and _normalized(result.chunk.metadata.product_name) != expected_product
    )
    version_mismatches = sum(
        1
        for result in top_results
        if expected_version is not None
        and _normalized(result.chunk.metadata.target_version) != expected_version
    )
    return QueryEvaluation(
        query_id=case.query_id,
        top_k=top_k,
        precision_at_k=precision,
        recall_at_k=recall,
        reciprocal_rank=reciprocal_rank,
        ndcg_at_k=ndcg,
        first_relevant_rank=first_relevant_rank,
        product_confusion_count=product_confusions,
        version_mismatch_count=version_mismatches,
        returned_document_count=len(top_results),
        search_time_ms=search_time_ms,
    )


def aggregate_evaluations(
    evaluations: Iterable[QueryEvaluation],
) -> dict[str, float | int]:
    items = list(evaluations)
    if not items:
        return {
            "query_count": 0,
            "mean_precision_at_k": 0.0,
            "mean_recall_at_k": 0.0,
            "mrr": 0.0,
            "mean_ndcg_at_k": 0.0,
            "mean_search_time_ms": 0.0,
            "product_confusion_count": 0,
            "version_mismatch_count": 0,
        }
    return {
        "query_count": len(items),
        "mean_precision_at_k": fmean(item.precision_at_k for item in items),
        "mean_recall_at_k": fmean(item.recall_at_k for item in items),
        "mrr": fmean(item.reciprocal_rank for item in items),
        "mean_ndcg_at_k": fmean(item.ndcg_at_k for item in items),
        "mean_search_time_ms": fmean(item.search_time_ms for item in items),
        "product_confusion_count": sum(
            item.product_confusion_count for item in items
        ),
        "version_mismatch_count": sum(
            item.version_mismatch_count for item in items
        ),
    }
