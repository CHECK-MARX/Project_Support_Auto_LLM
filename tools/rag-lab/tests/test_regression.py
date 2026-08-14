from __future__ import annotations

import json
from pathlib import Path

import pytest

from rag_lab.io import DataValidationError, PathValidationError
from rag_lab.regression import (
    RegressionThresholds,
    build_baseline_candidate,
    compare_reports,
    run_baseline_candidate_generation,
    run_baseline_candidate_review,
    run_regression_comparison,
    run_tracked_baseline_validation,
    validate_tracked_baseline,
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


def _tracked_baseline() -> dict[str, object]:
    row = _row()
    for metric in (
        "chunk_generation_time_ms",
        "index_build_time_ms",
        "mean_search_time_ms",
    ):
        row.pop(metric)
    row.update(document_count=2, chunk_count=2, query_count=2)
    return {
        "schema_version": 3,
        "data_classification": "synthetic",
        "report_name": "reference-baseline",
        "input_fingerprints": [
            {
                "file_name": "documents.json",
                "size_bytes": 100,
                "sha256": "a" * 64,
            }
        ],
        "summary": [row],
    }


def _generated_report(*, gate_passed: bool = True) -> dict[str, object]:
    report = _tracked_baseline()
    row = report["summary"][0]  # type: ignore[index]
    row.update(  # type: ignore[union-attr]
        chunk_generation_time_ms=1.0,
        index_build_time_ms=2.0,
        mean_search_time_ms=3.0,
        quality_gate_passed=gate_passed,
        quality_gate_violations=[],
    )
    report["quality_gate"] = {
        "status": "passed" if gate_passed else "failed",
        "recommended_configuration": {
            "chunk_strategy": row["chunk_strategy"],  # type: ignore[index]
            "search_method": row["search_method"],  # type: ignore[index]
            "reranker": row["reranker"],  # type: ignore[index]
            "filter_mode": row["filter_mode"],  # type: ignore[index]
            "top_k": row["top_k"],  # type: ignore[index]
        },
    }
    report["generated_at_utc"] = "2026-01-01T00:00:00+00:00"
    report["details"] = [{"query": "synthetic detail that must not be copied"}]
    return report


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
    baseline = _tracked_baseline()
    (baselines / "reference.json").write_text(
        json.dumps(baseline), encoding="utf-8"
    )
    (generated / "candidate.json").write_text(
        json.dumps(baseline), encoding="utf-8"
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


def test_comparison_rejects_unsafe_tracked_baseline(tmp_path: Path) -> None:
    baselines = tmp_path / "baselines"
    generated = tmp_path / "reports" / "generated"
    baselines.mkdir()
    generated.mkdir(parents=True)
    unsafe = _tracked_baseline()
    unsafe["source_text"] = "must not be tracked"
    (baselines / "unsafe.json").write_text(json.dumps(unsafe), encoding="utf-8")
    (generated / "candidate.json").write_text(
        json.dumps(_tracked_baseline()), encoding="utf-8"
    )

    with pytest.raises(DataValidationError, match="unexpected fields"):
        run_regression_comparison(
            tmp_path,
            baseline_path="baselines/unsafe.json",
            candidate_path="candidate.json",
        )


def test_tracked_baseline_safe_schema_is_accepted(tmp_path: Path) -> None:
    baselines = tmp_path / "baselines"
    baselines.mkdir()
    source = baselines / "reference.json"
    source.write_text(json.dumps(_tracked_baseline()), encoding="utf-8")

    output = run_tracked_baseline_validation(
        tmp_path,
        baseline_path="baselines/reference.json",
    )

    assert output.file == source
    assert output.fingerprint_count == 1
    assert output.configuration_count == 1


@pytest.mark.parametrize(
    ("target", "field"),
    (
        ("root", "source_text"),
        ("summary", "source_path"),
        ("summary", "mean_search_time_ms"),
    ),
)
def test_tracked_baseline_rejects_unexpected_fields(
    target: str, field: str
) -> None:
    baseline = _tracked_baseline()
    if target == "root":
        baseline[field] = "must not be tracked"
    else:
        baseline["summary"][0][field] = "must not be tracked"  # type: ignore[index]

    with pytest.raises(DataValidationError, match="unexpected fields"):
        validate_tracked_baseline(baseline)


def test_tracked_baseline_rejects_path_and_invalid_fingerprint() -> None:
    baseline = _tracked_baseline()
    fingerprint = baseline["input_fingerprints"][0]  # type: ignore[index]
    fingerprint["file_name"] = r"C:\customer\documents.json"

    with pytest.raises(DataValidationError, match="base file name"):
        validate_tracked_baseline(baseline)

    fingerprint["file_name"] = "documents.json"
    fingerprint["sha256"] = "NOT-A-SHA"
    with pytest.raises(DataValidationError, match="sha256"):
        validate_tracked_baseline(baseline)


def test_tracked_baseline_rejects_invalid_metric_and_duplicate_configuration() -> None:
    baseline = _tracked_baseline()
    baseline["summary"][0]["mrr"] = 1.01  # type: ignore[index]
    with pytest.raises(DataValidationError, match="outside 0..1"):
        validate_tracked_baseline(baseline)

    baseline = _tracked_baseline()
    baseline["summary"].append(dict(baseline["summary"][0]))  # type: ignore[union-attr,index]
    with pytest.raises(DataValidationError, match="duplicate configurations"):
        validate_tracked_baseline(baseline)


def test_baseline_validation_runner_rejects_files_outside_baselines(
    tmp_path: Path,
) -> None:
    (tmp_path / "baselines").mkdir()
    outside = tmp_path / "outside.json"
    outside.write_text(json.dumps(_tracked_baseline()), encoding="utf-8")

    with pytest.raises(PathValidationError, match="outside"):
        run_tracked_baseline_validation(
            tmp_path,
            baseline_path=outside,
        )


def test_baseline_candidate_copies_only_safe_recommended_summary() -> None:
    candidate = build_baseline_candidate(
        _generated_report(),
        report_name="review-candidate",
    )

    assert set(candidate) == {
        "schema_version",
        "data_classification",
        "report_name",
        "input_fingerprints",
        "summary",
    }
    assert candidate["report_name"] == "review-candidate"
    row = candidate["summary"][0]
    assert "mean_search_time_ms" not in row
    assert "quality_gate_passed" not in row
    assert "details" not in candidate
    validate_tracked_baseline(candidate)


def test_baseline_candidate_requires_passing_gate_and_matching_row() -> None:
    with pytest.raises(DataValidationError, match="passing quality gate"):
        build_baseline_candidate(
            _generated_report(gate_passed=False),
            report_name="review-candidate",
        )

    report = _generated_report()
    recommendation = report["quality_gate"]["recommended_configuration"]  # type: ignore[index]
    recommendation["search_method"] = "keyword"  # type: ignore[index]
    with pytest.raises(DataValidationError, match="not present"):
        build_baseline_candidate(report, report_name="review-candidate")

    report = _generated_report()
    report["summary"][0]["quality_gate_passed"] = False  # type: ignore[index]
    with pytest.raises(DataValidationError, match="row-level quality gate"):
        build_baseline_candidate(report, report_name="review-candidate")


def test_baseline_candidate_runner_writes_only_generated_output(
    tmp_path: Path,
) -> None:
    generated = tmp_path / "reports" / "generated"
    generated.mkdir(parents=True)
    source = generated / "evaluation.json"
    source.write_text(json.dumps(_generated_report()), encoding="utf-8")

    output = run_baseline_candidate_generation(
        tmp_path,
        source_path="evaluation.json",
        output_name="review-candidate",
    )

    assert output.file == generated / "review-candidate.json"
    assert output.file.is_file()
    assert not (tmp_path / "baselines" / "review-candidate.json").exists()
    assert "synthetic detail that must not be copied" not in output.file.read_text(
        encoding="utf-8"
    )

    with pytest.raises(PathValidationError, match="outside"):
        run_baseline_candidate_generation(
            tmp_path,
            source_path=tmp_path / "outside.json",
        )

    with pytest.raises(ValueError, match="must not overwrite"):
        run_baseline_candidate_generation(
            tmp_path,
            source_path="evaluation.json",
            output_name="evaluation",
        )


def test_baseline_candidate_review_passes_and_reports_regression(
    tmp_path: Path,
) -> None:
    baselines = tmp_path / "baselines"
    generated = tmp_path / "reports" / "generated"
    baselines.mkdir()
    generated.mkdir(parents=True)
    baseline = _tracked_baseline()
    (baselines / "reference.json").write_text(
        json.dumps(baseline), encoding="utf-8"
    )
    candidate_path = generated / "candidate.json"
    candidate_path.write_text(json.dumps(baseline), encoding="utf-8")

    output = run_baseline_candidate_review(
        tmp_path,
        baseline_path="baselines/reference.json",
        candidate_path="candidate.json",
        output_name="candidate-review",
    )

    assert output.report["status"] == "passed"
    assert output.report["counts"]["unchanged"] == 1
    assert set(output.files) == {"json", "markdown"}
    assert all(path.parent == generated for path in output.files.values())

    candidate = json.loads(json.dumps(baseline))
    candidate["summary"][0]["mrr"] = 0.5
    candidate_path.write_text(json.dumps(candidate), encoding="utf-8")
    regressed = run_baseline_candidate_review(
        tmp_path,
        baseline_path="baselines/reference.json",
        candidate_path="candidate.json",
        output_name="regressed-review",
    )

    assert regressed.report["status"] == "failed"
    assert regressed.report["counts"]["regressed"] == 1


def test_baseline_candidate_review_rejects_unsafe_candidate_and_paths(
    tmp_path: Path,
) -> None:
    baselines = tmp_path / "baselines"
    generated = tmp_path / "reports" / "generated"
    baselines.mkdir()
    generated.mkdir(parents=True)
    baseline = _tracked_baseline()
    (baselines / "reference.json").write_text(
        json.dumps(baseline), encoding="utf-8"
    )
    unsafe = dict(baseline)
    unsafe["details"] = [{"query": "must not be accepted"}]
    (generated / "unsafe.json").write_text(json.dumps(unsafe), encoding="utf-8")

    with pytest.raises(DataValidationError, match="unexpected fields"):
        run_baseline_candidate_review(
            tmp_path,
            baseline_path="baselines/reference.json",
            candidate_path="unsafe.json",
        )

    with pytest.raises(PathValidationError, match="outside"):
        run_baseline_candidate_review(
            tmp_path,
            baseline_path=tmp_path / "outside.json",
            candidate_path="unsafe.json",
        )

    with pytest.raises(PathValidationError, match="outside"):
        run_baseline_candidate_review(
            tmp_path,
            baseline_path="baselines/reference.json",
            candidate_path=tmp_path / "outside.json",
        )

    with pytest.raises(ValueError, match="must not overwrite"):
        run_baseline_candidate_review(
            tmp_path,
            baseline_path="baselines/reference.json",
            candidate_path="unsafe.json",
            output_name="unsafe",
        )
