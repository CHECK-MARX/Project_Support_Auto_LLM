from __future__ import annotations

import json
from pathlib import Path

import pytest

from rag_lab.baseline_readiness import (
    assess_baseline_readiness,
    compare_candidate_reproducibility,
    run_baseline_readiness_assessment,
    run_candidate_reproducibility_check,
)
from rag_lab.io import DataValidationError, PathValidationError


def _candidate(report_name: str = "candidate") -> dict[str, object]:
    return {
        "schema_version": 3,
        "data_classification": "synthetic",
        "report_name": report_name,
        "input_fingerprints": [
            {
                "file_name": "documents.json",
                "size_bytes": 100,
                "sha256": "a" * 64,
            }
        ],
        "summary": [
            {
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
                "document_count": 1,
                "chunk_count": 1,
                "query_count": 1,
                "excluded_term_hit_count": 0,
                "product_confusion_count": 0,
                "version_mismatch_count": 0,
            }
        ],
    }


def _write_inputs(root: Path) -> tuple[Path, Path, Path]:
    baselines = root / "baselines"
    generated = root / "reports" / "generated"
    baselines.mkdir()
    generated.mkdir(parents=True)
    baseline = baselines / "reference.json"
    candidate = generated / "candidate.json"
    reproduction = generated / "reproduction.json"
    baseline.write_text(json.dumps(_candidate("reference")), encoding="utf-8")
    candidate.write_text(json.dumps(_candidate("candidate")), encoding="utf-8")
    reproduction.write_text(
        json.dumps(_candidate("reproduction")), encoding="utf-8"
    )
    return baseline, candidate, reproduction


def test_reproducibility_ignores_only_report_name() -> None:
    report = compare_candidate_reproducibility(
        _candidate("first"),
        _candidate("second"),
        first_file_name="first.json",
        second_file_name="second.json",
    )

    assert report["status"] == "passed"
    assert report["content_match"] is True
    assert report["first_digest"] == report["second_digest"]


def test_reproducibility_rejects_content_change_and_unsafe_candidate() -> None:
    changed = _candidate("second")
    changed["summary"][0]["mrr"] = 0.5  # type: ignore[index]
    report = compare_candidate_reproducibility(
        _candidate("first"),
        changed,
        first_file_name="first.json",
        second_file_name="second.json",
    )

    assert report["status"] == "failed"
    assert report["content_match"] is False
    assert report["findings"] == ["canonical candidate contents differ"]

    unsafe = _candidate()
    unsafe["details"] = [{"query": "must not be accepted"}]
    with pytest.raises(DataValidationError, match="unexpected fields"):
        compare_candidate_reproducibility(
            unsafe,
            _candidate(),
            first_file_name="unsafe.json",
            second_file_name="candidate.json",
        )


def test_reproducibility_runner_writes_only_generated_reports(tmp_path: Path) -> None:
    _, candidate, reproduction = _write_inputs(tmp_path)

    output = run_candidate_reproducibility_check(
        tmp_path,
        first_path=candidate.name,
        second_path=reproduction.name,
        output_name="reproducibility-check",
    )

    assert output.report["status"] == "passed"
    assert set(output.files) == {"json", "markdown"}
    assert all(path.parent == candidate.parent for path in output.files.values())

    with pytest.raises(PathValidationError, match="outside"):
        run_candidate_reproducibility_check(
            tmp_path,
            first_path=tmp_path / "outside.json",
            second_path=reproduction.name,
        )
    with pytest.raises(ValueError, match="must not overwrite"):
        run_candidate_reproducibility_check(
            tmp_path,
            first_path=candidate.name,
            second_path=reproduction.name,
            output_name="candidate",
        )
    with pytest.raises(ValueError, match="two different"):
        run_candidate_reproducibility_check(
            tmp_path,
            first_path=candidate.name,
            second_path=candidate.name,
        )


def test_readiness_requires_regression_and_reproducibility_to_pass() -> None:
    ready = assess_baseline_readiness(
        _candidate("reference"),
        _candidate("candidate"),
        _candidate("reproduction"),
        baseline_file_name="reference.json",
        candidate_file_name="candidate.json",
        reproduction_file_name="reproduction.json",
    )

    assert ready["status"] == "ready"
    assert ready["reproducible"] is True
    assert ready["input_fingerprints_match"] is True
    assert ready["findings"] == []

    regressed = _candidate("candidate")
    regressed["summary"][0]["mrr"] = 0.5  # type: ignore[index]
    blocked = assess_baseline_readiness(
        _candidate("reference"),
        regressed,
        regressed,
        baseline_file_name="reference.json",
        candidate_file_name="candidate.json",
        reproduction_file_name="reproduction.json",
    )
    assert blocked["status"] == "blocked"
    assert blocked["regression_counts"]["regressed"] == 1

    reproduction_changed = _candidate("reproduction")
    reproduction_changed["summary"][0]["mrr"] = 0.5  # type: ignore[index]
    not_reproducible = assess_baseline_readiness(
        _candidate("reference"),
        _candidate("candidate"),
        reproduction_changed,
        baseline_file_name="reference.json",
        candidate_file_name="candidate.json",
        reproduction_file_name="reproduction.json",
    )
    assert not_reproducible["status"] == "blocked"
    assert not_reproducible["reproducible"] is False


def test_readiness_runner_keeps_tracked_baseline_read_only(tmp_path: Path) -> None:
    baseline, candidate, reproduction = _write_inputs(tmp_path)
    before = baseline.read_bytes()

    output = run_baseline_readiness_assessment(
        tmp_path,
        baseline_path="baselines/reference.json",
        candidate_path=candidate.name,
        reproduction_path=reproduction.name,
        output_name="readiness",
    )

    assert output.report["status"] == "ready"
    assert baseline.read_bytes() == before
    assert all(path.parent == candidate.parent for path in output.files.values())

    with pytest.raises(PathValidationError, match="outside"):
        run_baseline_readiness_assessment(
            tmp_path,
            baseline_path=tmp_path / "outside.json",
            candidate_path=candidate.name,
            reproduction_path=reproduction.name,
        )
    with pytest.raises(ValueError, match="must not overwrite"):
        run_baseline_readiness_assessment(
            tmp_path,
            baseline_path="baselines/reference.json",
            candidate_path=candidate.name,
            reproduction_path=reproduction.name,
            output_name="candidate",
        )
    with pytest.raises(ValueError, match="two different"):
        run_baseline_readiness_assessment(
            tmp_path,
            baseline_path="baselines/reference.json",
            candidate_path=candidate.name,
            reproduction_path=candidate.name,
        )
