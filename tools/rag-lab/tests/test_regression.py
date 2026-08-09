from __future__ import annotations

import json
from pathlib import Path

import pytest

from rag_lab.io import DataValidationError, PathValidationError
from rag_lab.regression import (
    RegressionThresholds,
    compare_reports,
    run_regression_comparison,
)


def _row(**overrides: object) -> dict[str, object]:
    row: dict[str, object] = {
        "chunk_strategy": "fixed",
        "search_method": "bm25",
        "reranker": "none",
        "filter_mode": "product_and_version",
        "top_k": 3,
        "mean_precision_at_k": 1.0,
        "mean_recall_at_k": 1.0,
        "mrr": 1.0,
        "mean_ndcg_at_k": 1.0,
        "mean_required_term_coverage_at_k": 1.0,
        "excluded_term_hit_count": 0,
        "product_confusion_count": 0,
        "version_mismatch_count": 0,
        "chunk_generation_time_ms": 1.0,
        "index_build_time_ms": 2.0,
        "mean_search_time_ms": 3.0,
    }
    row.update(overrides)
    return row


def _report(rows: list[dict[str, object]], fingerprint: str = "same") -> dict[str, object]:
    return {
        "schema_version": 3,
        "data_classification": "synthetic",
        "input_fingerprints": [{"file_name": "data.json", "sha256": fingerprint}],
        "summary": rows,
    }


def test_comparison_reports_improvement_without_timing_failure() -> None:
    baseline = _report([_row(mrr=0.8, mean_search_time_ms=1.0)])
    candidate = _report([_row(mrr=1.0, mean_search_time_ms=50.0)])

    report = compare_reports(
        baseline,
        candidate,
        baseline_file_name="before.json",
        candidate_file_name="after.json",
    )

    assert report["status"] == "passed"
    assert report["counts"]["improved"] == 1
    assert report["rows"][0]["timings"]["mean_search_time_ms"]["delta"] == 49.0


def test_comparison_fails_for_quality_drop_and_missing_configuration() -> None:
    baseline = _report(
        [
            _row(),
            _row(chunk_strategy="heading"),
        ]
    )
    candidate = _report([_row(mean_recall_at_k=0.7)])

    report = compare_reports(
        baseline,
        candidate,
        baseline_file_name="before.json",
        candidate_file_name="after.json",
    )

    assert report["status"] == "failed"
    assert report["counts"]["regressed"] == 1
    assert report["counts"]["missing"] == 1
    assert any(
        "mean_recall_at_k" in finding
        for finding in report["rows"][0]["findings"]
    )


def test_threshold_and_explicit_input_change_can_be_allowed() -> None:
    baseline = _report([_row(mrr=1.0)], fingerprint="before")
    candidate = _report([_row(mrr=0.99)], fingerprint="after")

    report = compare_reports(
        baseline,
        candidate,
        baseline_file_name="before.json",
        candidate_file_name="after.json",
        thresholds=RegressionThresholds(max_quality_drop=0.02),
        allow_input_change=True,
    )

    assert report["status"] == "passed"
    assert report["input_fingerprints_match"] is False
    assert report["global_findings"] == []


def test_input_change_fails_by_default() -> None:
    report = compare_reports(
        _report([_row()], fingerprint="before"),
        _report([_row()], fingerprint="after"),
        baseline_file_name="before.json",
        candidate_file_name="after.json",
    )

    assert report["status"] == "failed"
    assert report["global_findings"] == ["input fingerprints differ"]


def test_old_report_schema_is_rejected() -> None:
    with pytest.raises(DataValidationError, match="schema_version"):
        compare_reports(
            {"schema_version": 2},
            _report([_row()]),
            baseline_file_name="before.json",
            candidate_file_name="after.json",
        )


def test_non_finite_metric_is_rejected() -> None:
    with pytest.raises(DataValidationError, match="invalid metric"):
        compare_reports(
            _report([_row(mrr=float("nan"))]),
            _report([_row()]),
            baseline_file_name="before.json",
            candidate_file_name="after.json",
        )


def test_runner_reads_and_writes_only_generated_reports(tmp_path: Path) -> None:
    generated = tmp_path / "reports" / "generated"
    generated.mkdir(parents=True)
    baseline = _report([_row()])
    candidate = _report([_row(mrr=0.9)])
    (generated / "before.json").write_text(
        json.dumps(baseline), encoding="utf-8"
    )
    (generated / "after.json").write_text(
        json.dumps(candidate), encoding="utf-8"
    )

    output = run_regression_comparison(
        tmp_path,
        baseline_path="before.json",
        candidate_path="after.json",
        output_name="result",
    )

    assert output.report["status"] == "failed"
    assert set(output.files) == {"json", "markdown"}
    assert all(path.parent == generated for path in output.files.values())
    serialized = output.files["json"].read_text(encoding="utf-8")
    assert str(tmp_path) not in serialized
    assert "before.json" in serialized

    with pytest.raises(PathValidationError, match="outside"):
        run_regression_comparison(
            tmp_path,
            baseline_path="../outside.json",
            candidate_path="after.json",
        )

    with pytest.raises(ValueError, match="must not overwrite"):
        run_regression_comparison(
            tmp_path,
            baseline_path="before.json",
            candidate_path="after.json",
            output_name="before",
        )


def test_runner_accepts_tracked_synthetic_baseline(tmp_path: Path) -> None:
    baselines = tmp_path / "baselines"
    generated = tmp_path / "reports" / "generated"
    baselines.mkdir()
    generated.mkdir(parents=True)
    (baselines / "reference.json").write_text(
        json.dumps(_report([_row()])), encoding="utf-8"
    )
    (generated / "candidate.json").write_text(
        json.dumps(_report([_row()])), encoding="utf-8"
    )

    output = run_regression_comparison(
        tmp_path,
        baseline_path="baselines/reference.json",
        candidate_path="candidate.json",
        output_name="tracked-result",
    )

    assert output.report["status"] == "passed"
    assert output.report["baseline_file_name"] == "reference.json"
    assert all(path.parent == generated for path in output.files.values())
