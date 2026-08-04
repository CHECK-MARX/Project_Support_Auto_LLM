import pytest

from rag_lab.evaluation import aggregate_evaluations, evaluate_results
from rag_lab.models import Chunk, DocumentMetadata, EvaluationCase
from rag_lab.search import SearchResult


def _result(
    document_id: str,
    rank: int,
    *,
    product: str,
    version: str,
    support_id: str,
) -> SearchResult:
    chunk = Chunk(
        chunk_id=f"{document_id}:{rank}",
        document_id=document_id,
        chunk_index=0,
        strategy="test",
        text="人工テキスト",
        metadata=DocumentMetadata(
            product_name=product,
            target_version=version,
            support_id=support_id,
        ),
    )
    return SearchResult(chunk, score=1.0 / rank, rank=rank)


def test_precision_recall_mrr_ndcg_and_correct_rank() -> None:
    case = EvaluationCase(
        query_id="q1",
        product="Checkmarx",
        query="質問",
        target_version="2.0",
        expected_document_ids=("correct",),
    )
    results = [
        _result(
            "wrong", 1, product="HelixQAC", version="1.0", support_id="SYN-X"
        ),
        _result(
            "correct",
            2,
            product="Checkmarx",
            version="2.0",
            support_id="SYN-1",
        ),
    ]

    metrics = evaluate_results(case, results, top_k=3, search_time_ms=2.5)

    assert metrics.precision_at_k == pytest.approx(1 / 3)
    assert metrics.recall_at_k == 1.0
    assert metrics.reciprocal_rank == 0.5
    assert metrics.first_relevant_rank == 2
    assert metrics.ndcg_at_k == pytest.approx(1 / 1.5849625007)
    assert metrics.product_confusion_count == 1
    assert metrics.version_mismatch_count == 1


def test_duplicate_chunks_do_not_change_document_level_metrics() -> None:
    case = EvaluationCase(
        query_id="q1",
        product="Product",
        query="質問",
        expected_document_ids=("correct",),
    )
    results = [
        _result("correct", 1, product="Product", version="1", support_id="SYN"),
        _result("correct", 2, product="Product", version="1", support_id="SYN"),
    ]

    metrics = evaluate_results(case, results, top_k=1)

    assert metrics.precision_at_k == 1.0
    assert metrics.returned_document_count == 1


def test_expected_support_id_is_used_when_document_ids_are_empty() -> None:
    case = EvaluationCase(
        query_id="q1",
        product="Product",
        query="質問",
        expected_support_ids=("SYN-2",),
    )

    metrics = evaluate_results(
        case,
        [_result("doc", 1, product="Product", version="1", support_id="SYN-2")],
        top_k=1,
    )

    assert metrics.recall_at_k == 1.0
    assert metrics.first_relevant_rank == 1


def test_aggregate_calculates_mrr_and_confusion_totals() -> None:
    first = evaluate_results(
        EvaluationCase(
            query_id="q1",
            product="Product",
            query="質問",
            expected_document_ids=("a",),
        ),
        [_result("a", 1, product="Product", version="1", support_id="S1")],
        top_k=1,
    )
    second = evaluate_results(
        EvaluationCase(
            query_id="q2",
            product="Product",
            query="質問",
            expected_document_ids=("b",),
        ),
        [_result("x", 1, product="Other", version="1", support_id="S2")],
        top_k=1,
    )

    aggregate = aggregate_evaluations([first, second])

    assert aggregate["query_count"] == 2
    assert aggregate["mrr"] == 0.5
    assert aggregate["product_confusion_count"] == 1
