import pytest

from rag_lab.quality import (
    QualityGateThresholds,
    apply_quality_gate,
    assess_summary_row,
)


def _row(**overrides: object) -> dict[str, object]:
    row: dict[str, object] = {
        "chunk_strategy": "heading",
        "search_method": "hybrid",
        "reranker": "lexical",
        "filter_mode": "product_and_version",
        "top_k": 3,
        "mean_recall_at_k": 1.0,
        "mrr": 1.0,
        "mean_ndcg_at_k": 1.0,
        "mean_required_term_coverage_at_k": 1.0,
        "excluded_term_hit_count": 0,
        "product_confusion_count": 0,
        "version_mismatch_count": 0,
        "mean_search_time_ms": 2.0,
    }
    row.update(overrides)
    return row


def test_quality_gate_reports_each_failed_threshold() -> None:
    passed, violations = assess_summary_row(
        _row(
            mean_recall_at_k=0.5,
            mean_required_term_coverage_at_k=0.4,
            excluded_term_hit_count=1,
            product_confusion_count=1,
        ),
        QualityGateThresholds(),
    )

    assert passed is False
    assert len(violations) == 4
    assert any("Recall" in violation for violation in violations)
    assert any("excluded-term" in violation for violation in violations)


def test_recommendation_prefers_safe_filter_when_quality_is_equal() -> None:
    rows = [
        _row(filter_mode="none", mean_search_time_ms=1.0),
        _row(filter_mode="product_and_version", mean_search_time_ms=2.0),
    ]

    annotated, gate = apply_quality_gate(
        rows, QualityGateThresholds(), preferred_top_k=3
    )

    assert all(row["quality_gate_passed"] for row in annotated)
    assert gate["status"] == "passed"
    assert gate["recommended_configuration"]["filter_mode"] == (
        "product_and_version"
    )


def test_quality_gate_fails_when_no_candidate_passes() -> None:
    _, gate = apply_quality_gate(
        [_row(mean_recall_at_k=0.0)],
        QualityGateThresholds(),
        preferred_top_k=3,
    )

    assert gate["status"] == "failed"
    assert gate["passing_candidate_count"] == 0


def test_preferred_top_k_must_exist() -> None:
    with pytest.raises(ValueError, match="not present"):
        apply_quality_gate(
            [_row(top_k=1)], QualityGateThresholds(), preferred_top_k=3
        )
