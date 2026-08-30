import pytest

from rag_lab.evaluation import aggregate_evaluations, evaluate_results
from rag_lab.models import Chunk, DocumentMetadata, EvaluationCase
from rag_lab.search import SearchResult
from rag_lab.topic_evaluation import load_topic_metadata


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


def test_required_and_excluded_terms_are_checked_in_top_k_text() -> None:
    case = EvaluationCase(
        query_id="q-terms",
        product="Product",
        query="質問",
        expected_document_ids=("doc",),
        required_terms=("Canonical Path", "allow list"),
        excluded_terms=("HelixQAC",),
    )
    result = _result(
        "doc", 1, product="Product", version="1", support_id="SYN"
    )
    result = SearchResult(
        chunk=Chunk(
            chunk_id=result.chunk.chunk_id,
            document_id=result.chunk.document_id,
            chunk_index=0,
            strategy="test",
            text="Canonical Pathを確認します。HelixQACは対象外です。",
            metadata=result.chunk.metadata,
        ),
        score=1.0,
        rank=1,
    )

    metrics = evaluate_results(case, [result], top_k=1)

    assert metrics.required_term_coverage_at_k == 0.5
    assert metrics.required_terms_missing == ("allow list",)
    assert metrics.excluded_terms_found == ("HelixQAC",)
    assert metrics.excluded_query_terms_found == ()


def test_query_privacy_gate_counts_redacted_categories_not_evidence_terms() -> None:
    case = EvaluationCase(
        query_id="q-private",
        product="Product",
        query="株式会社匿名顧客 担当者: 山田 03-1234-5678 test@example.invalid Validate Stream設定",
        expected_document_ids=("doc",),
        excluded_terms=("permission",),
    )
    result = _result(
        "doc", 1, product="Product", version="1", support_id="SYN"
    )
    result = SearchResult(
        chunk=Chunk(
            chunk_id=result.chunk.chunk_id,
            document_id=result.chunk.document_id,
            chunk_index=0,
            strategy="test",
            text="Validate Stream設定。permission は別トピックです。",
            metadata=result.chunk.metadata,
        ),
        score=1.0,
        rank=1,
    )

    metrics = evaluate_results(case, [result], top_k=1)
    aggregate = aggregate_evaluations([metrics])

    assert metrics.excluded_terms_found == ("permission",)
    assert set(metrics.excluded_query_terms_found) == {
        "company_name",
        "recipient_label",
        "phone_or_extension",
        "email",
    }
    assert aggregate["excluded_term_hit_count"] == 4
    assert aggregate["forbidden_evidence_term_hit_count"] == 1


def test_phase22_generic_supported_languages_policy_allows_engine_pack() -> None:
    metadata = load_topic_metadata(
        "samples/phase22_topic_metadata.json"
    )
    case = EvaluationCase(
        query_id="p22-46",
        product="Checkmarx",
        feature="Supported Languages",
        query="対応言語",
    )

    policy = metadata.policy_for(case)

    assert "Engine Pack" in policy.allowed_related_topics
    assert "Supported Languages" in policy.required_topics
