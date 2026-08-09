from __future__ import annotations

from dataclasses import dataclass
import math
from pathlib import Path
import re
from typing import Any, Mapping

from .io import (
    DataValidationError,
    LabPaths,
    PathValidationError,
    read_generated_json,
    read_json,
    write_json_report,
)
from .reporting import write_regression_reports


_IDENTITY_FIELDS = (
    "chunk_strategy",
    "search_method",
    "reranker",
    "filter_mode",
    "top_k",
)
_QUALITY_METRICS = (
    "mean_precision_at_k",
    "mean_recall_at_k",
    "mrr",
    "mean_ndcg_at_k",
    "mean_required_term_coverage_at_k",
)
_COUNT_METRICS = (
    "excluded_term_hit_count",
    "product_confusion_count",
    "version_mismatch_count",
)
_TIMING_METRICS = (
    "chunk_generation_time_ms",
    "index_build_time_ms",
    "mean_search_time_ms",
)
_BASELINE_ROOT_FIELDS = {
    "schema_version",
    "data_classification",
    "report_name",
    "input_fingerprints",
    "summary",
}
_FINGERPRINT_FIELD_ORDER = ("file_name", "size_bytes", "sha256")
_FINGERPRINT_FIELDS = set(_FINGERPRINT_FIELD_ORDER)
_BASELINE_COUNT_METRICS = (
    "document_count",
    "chunk_count",
    "query_count",
    *_COUNT_METRICS,
)
_BASELINE_SUMMARY_FIELD_ORDER = (
    *_IDENTITY_FIELDS,
    *_QUALITY_METRICS,
    *_BASELINE_COUNT_METRICS,
)
_BASELINE_SUMMARY_FIELDS = set(_BASELINE_SUMMARY_FIELD_ORDER)
_SAFE_REPORT_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}\Z")
_SHA256 = re.compile(r"[0-9a-f]{64}\Z")
_ALLOWED_IDENTITY_VALUES = {
    "chunk_strategy": {"fixed", "paragraph", "heading", "structured"},
    "search_method": {"keyword", "bm25", "hash_embedding", "hybrid"},
    "reranker": {"none", "lexical"},
    "filter_mode": {"none", "product", "product_and_version"},
}


@dataclass(frozen=True, slots=True)
class RegressionThresholds:
    max_quality_drop: float = 0.0
    max_count_increase: int = 0

    def __post_init__(self) -> None:
        if not math.isfinite(self.max_quality_drop) or self.max_quality_drop < 0.0:
            raise ValueError("max_quality_drop must be zero or greater")
        if (
            not isinstance(self.max_count_increase, int)
            or isinstance(self.max_count_increase, bool)
            or self.max_count_increase < 0
        ):
            raise ValueError("max_count_increase must be zero or greater")


@dataclass(frozen=True, slots=True)
class RegressionRunOutput:
    report: dict[str, Any]
    files: dict[str, Path]


@dataclass(frozen=True, slots=True)
class BaselineValidationOutput:
    file: Path
    fingerprint_count: int
    configuration_count: int


@dataclass(frozen=True, slots=True)
class BaselineCandidateOutput:
    candidate: dict[str, Any]
    file: Path


@dataclass(frozen=True, slots=True)
class BaselineReviewOutput:
    report: dict[str, Any]
    files: dict[str, Path]


def _require_exact_fields(
    value: Mapping[str, Any], allowed: set[str], label: str
) -> None:
    actual = set(value)
    missing = sorted(allowed - actual)
    unexpected = sorted(actual - allowed)
    if missing:
        raise DataValidationError(f"{label} is missing fields: {', '.join(missing)}")
    if unexpected:
        raise DataValidationError(
            f"{label} contains unexpected fields: {', '.join(unexpected)}"
        )


def validate_tracked_baseline(report: Any) -> tuple[int, int]:
    """Validate the compact, synthetic-only schema allowed in source control."""
    if not isinstance(report, Mapping):
        raise DataValidationError("baseline root must be an object")
    _require_exact_fields(report, _BASELINE_ROOT_FIELDS, "baseline root")
    if report["schema_version"] != 3:
        raise DataValidationError("baseline schema_version must be exactly 3")
    if report["data_classification"] != "synthetic":
        raise DataValidationError("baseline must contain synthetic data only")
    report_name = report["report_name"]
    if not isinstance(report_name, str) or not _SAFE_REPORT_NAME.fullmatch(report_name):
        raise DataValidationError("baseline report_name is not a safe simple name")

    fingerprints = report["input_fingerprints"]
    if not isinstance(fingerprints, list) or not fingerprints:
        raise DataValidationError("baseline input_fingerprints must be non-empty")
    seen_files: set[str] = set()
    for index, fingerprint in enumerate(fingerprints):
        label = f"baseline fingerprint {index}"
        if not isinstance(fingerprint, Mapping):
            raise DataValidationError(f"{label} must be an object")
        _require_exact_fields(fingerprint, _FINGERPRINT_FIELDS, label)
        file_name = fingerprint["file_name"]
        if (
            not isinstance(file_name, str)
            or not file_name
            or len(file_name) > 255
            or "/" in file_name
            or "\\" in file_name
            or Path(file_name).name != file_name
        ):
            raise DataValidationError(f"{label} file_name must be a base file name")
        if file_name.casefold() in seen_files:
            raise DataValidationError("baseline contains duplicate fingerprint files")
        seen_files.add(file_name.casefold())
        size_bytes = fingerprint["size_bytes"]
        if (
            not isinstance(size_bytes, int)
            or isinstance(size_bytes, bool)
            or size_bytes < 0
        ):
            raise DataValidationError(f"{label} size_bytes must be a non-negative integer")
        sha256 = fingerprint["sha256"]
        if not isinstance(sha256, str) or not _SHA256.fullmatch(sha256):
            raise DataValidationError(f"{label} sha256 must be lowercase hexadecimal")

    summary = report["summary"]
    if not isinstance(summary, list) or not summary:
        raise DataValidationError("baseline summary must be non-empty")
    seen_configurations: set[tuple[str, str, str, str, int]] = set()
    for index, row in enumerate(summary):
        label = f"baseline summary row {index}"
        if not isinstance(row, Mapping):
            raise DataValidationError(f"{label} must be an object")
        _require_exact_fields(row, _BASELINE_SUMMARY_FIELDS, label)
        identity = _identity(row, label)
        for field, allowed in _ALLOWED_IDENTITY_VALUES.items():
            if row[field] not in allowed:
                raise DataValidationError(f"{label} has unsupported {field}")
        if identity in seen_configurations:
            raise DataValidationError("baseline contains duplicate configurations")
        seen_configurations.add(identity)
        for metric in _QUALITY_METRICS:
            value = _number(row, metric, label)
            if not 0.0 <= value <= 1.0:
                raise DataValidationError(f"{label} metric is outside 0..1: {metric}")
        for metric in _BASELINE_COUNT_METRICS:
            value = row[metric]
            if (
                not isinstance(value, int)
                or isinstance(value, bool)
                or value < 0
            ):
                raise DataValidationError(
                    f"{label} count must be a non-negative integer: {metric}"
                )
    return len(fingerprints), len(summary)


def run_tracked_baseline_validation(
    lab_root: str | Path, *, baseline_path: str | Path
) -> BaselineValidationOutput:
    paths = LabPaths.create(lab_root, input_roots=("baselines",))
    source = paths.resolve_input(baseline_path)
    report = read_json(paths, source)
    fingerprint_count, configuration_count = validate_tracked_baseline(report)
    return BaselineValidationOutput(
        file=source,
        fingerprint_count=fingerprint_count,
        configuration_count=configuration_count,
    )


def build_baseline_candidate(report: Any, *, report_name: str) -> dict[str, Any]:
    if not isinstance(report, Mapping):
        raise DataValidationError("evaluation report root must be an object")
    if not _SAFE_REPORT_NAME.fullmatch(report_name):
        raise DataValidationError("baseline candidate report_name is not safe")
    if report.get("data_classification") != "synthetic":
        raise DataValidationError("baseline candidate source must be synthetic")
    quality_gate = report.get("quality_gate")
    if not isinstance(quality_gate, Mapping):
        raise DataValidationError("evaluation report quality_gate must be an object")
    if quality_gate.get("status") != "passed":
        raise DataValidationError("baseline candidate requires a passing quality gate")
    recommended = quality_gate.get("recommended_configuration")
    if not isinstance(recommended, Mapping):
        raise DataValidationError(
            "quality_gate recommended_configuration must be an object"
        )

    rows = _rows_by_identity(
        _validated_summary(report, "evaluation"), "evaluation"
    )
    recommended_identity = _identity(recommended, "recommended configuration")
    selected = rows.get(recommended_identity)
    if selected is None:
        raise DataValidationError(
            "recommended configuration is not present in evaluation summary"
        )
    if selected.get("quality_gate_passed") is not True:
        raise DataValidationError(
            "recommended configuration must pass its row-level quality gate"
        )

    fingerprints = report.get("input_fingerprints")
    try:
        compact_fingerprints = [
            {field: item[field] for field in _FINGERPRINT_FIELD_ORDER}
            for item in fingerprints
        ]
        compact_row = {
            field: selected[field] for field in _BASELINE_SUMMARY_FIELD_ORDER
        }
    except (KeyError, TypeError) as error:
        raise DataValidationError(
            "evaluation report cannot be reduced to the safe baseline schema"
        ) from error

    candidate = {
        "schema_version": 3,
        "data_classification": "synthetic",
        "report_name": report_name,
        "input_fingerprints": compact_fingerprints,
        "summary": [compact_row],
    }
    validate_tracked_baseline(candidate)
    return candidate


def run_baseline_candidate_generation(
    lab_root: str | Path,
    *,
    source_path: str | Path,
    output_name: str = "baseline-candidate",
) -> BaselineCandidateOutput:
    paths = LabPaths.create(lab_root)
    output_file_name = f"{output_name}.json"
    if Path(source_path).name.casefold() == Path(output_file_name).name.casefold():
        raise ValueError("baseline candidate must not overwrite its source report")
    report = read_generated_json(paths, source_path)
    candidate = build_baseline_candidate(report, report_name=output_name)
    file = write_json_report(paths, output_file_name, candidate)
    return BaselineCandidateOutput(candidate=candidate, file=file)


def run_baseline_candidate_review(
    lab_root: str | Path,
    *,
    baseline_path: str | Path,
    candidate_path: str | Path,
    output_name: str = "baseline-review",
) -> BaselineReviewOutput:
    paths = LabPaths.create(lab_root)
    baseline_paths = LabPaths.create(lab_root, input_roots=("baselines",))
    output_json_name = f"{output_name}.json"
    if Path(candidate_path).name.casefold() == Path(output_json_name).name.casefold():
        raise ValueError("baseline review must not overwrite its candidate")

    baseline = read_json(baseline_paths, baseline_path)
    candidate = read_generated_json(paths, candidate_path)
    validate_tracked_baseline(baseline)
    validate_tracked_baseline(candidate)
    report = compare_reports(
        baseline,
        candidate,
        baseline_file_name=Path(baseline_path).name,
        candidate_file_name=Path(candidate_path).name,
    )
    files = write_regression_reports(paths, output_name, report)
    return BaselineReviewOutput(report=report, files=files)


def _validated_summary(report: Any, label: str) -> list[Mapping[str, Any]]:
    if not isinstance(report, Mapping):
        raise DataValidationError(f"{label} report root must be an object")
    schema_version = report.get("schema_version")
    if (
        not isinstance(schema_version, int)
        or isinstance(schema_version, bool)
        or schema_version < 3
    ):
        raise DataValidationError(f"{label} report schema_version must be 3 or newer")
    if report.get("data_classification") != "synthetic":
        raise DataValidationError(f"{label} report must contain synthetic data only")
    fingerprints = report.get("input_fingerprints")
    if not isinstance(fingerprints, list) or not fingerprints or not all(
        isinstance(item, Mapping) for item in fingerprints
    ):
        raise DataValidationError(
            f"{label} report input_fingerprints must be a non-empty array"
        )
    summary = report.get("summary")
    if not isinstance(summary, list) or not summary:
        raise DataValidationError(f"{label} report summary must be a non-empty array")
    if not all(isinstance(row, Mapping) for row in summary):
        raise DataValidationError(f"{label} report summary rows must be objects")
    return summary


def _identity(row: Mapping[str, Any], label: str) -> tuple[str, str, str, str, int]:
    text_values = tuple(row.get(name) for name in _IDENTITY_FIELDS[:-1])
    top_k = row.get("top_k")
    if not all(isinstance(value, str) and value for value in text_values):
        raise DataValidationError(f"{label} row has invalid configuration identity")
    if not isinstance(top_k, int) or isinstance(top_k, bool) or top_k <= 0:
        raise DataValidationError(f"{label} row has invalid top_k")
    return (*text_values, top_k)


def _number(row: Mapping[str, Any], metric: str, label: str) -> float:
    value = row.get(metric)
    if (
        not isinstance(value, (int, float))
        or isinstance(value, bool)
        or not math.isfinite(value)
    ):
        raise DataValidationError(f"{label} row has invalid metric: {metric}")
    return float(value)


def _rows_by_identity(
    rows: list[Mapping[str, Any]], label: str
) -> dict[tuple[str, str, str, str, int], Mapping[str, Any]]:
    indexed: dict[tuple[str, str, str, str, int], Mapping[str, Any]] = {}
    for row in rows:
        identity = _identity(row, label)
        if identity in indexed:
            raise DataValidationError(f"{label} report contains duplicate configurations")
        for metric in (*_QUALITY_METRICS, *_COUNT_METRICS):
            _number(row, metric, label)
        indexed[identity] = row
    return indexed


def _configuration(identity: tuple[str, str, str, str, int]) -> dict[str, Any]:
    return dict(zip(_IDENTITY_FIELDS, identity, strict=True))


def _metric_delta(
    baseline: Mapping[str, Any], candidate: Mapping[str, Any], metric: str
) -> dict[str, float]:
    baseline_value = _number(baseline, metric, "baseline")
    candidate_value = _number(candidate, metric, "candidate")
    return {
        "baseline": baseline_value,
        "candidate": candidate_value,
        "delta": candidate_value - baseline_value,
    }


def compare_reports(
    baseline_report: Any,
    candidate_report: Any,
    *,
    baseline_file_name: str,
    candidate_file_name: str,
    thresholds: RegressionThresholds | None = None,
    allow_input_change: bool = False,
) -> dict[str, Any]:
    selected_thresholds = thresholds or RegressionThresholds()
    baseline_rows = _rows_by_identity(
        _validated_summary(baseline_report, "baseline"), "baseline"
    )
    candidate_rows = _rows_by_identity(
        _validated_summary(candidate_report, "candidate"), "candidate"
    )
    baseline_fingerprints = baseline_report.get("input_fingerprints")
    candidate_fingerprints = candidate_report.get("input_fingerprints")
    fingerprints_match = baseline_fingerprints == candidate_fingerprints
    global_findings: list[str] = []
    if not fingerprints_match and not allow_input_change:
        global_findings.append("input fingerprints differ")

    rows: list[dict[str, Any]] = []
    for identity in sorted(baseline_rows):
        baseline = baseline_rows[identity]
        candidate = candidate_rows.get(identity)
        if candidate is None:
            rows.append(
                {
                    "configuration": _configuration(identity),
                    "status": "missing",
                    "findings": ["configuration is missing from candidate"],
                    "metrics": {},
                    "timings": {},
                }
            )
            continue

        findings: list[str] = []
        metrics: dict[str, dict[str, float]] = {}
        improved = False
        for metric in _QUALITY_METRICS:
            comparison = _metric_delta(baseline, candidate, metric)
            metrics[metric] = comparison
            if comparison["delta"] < -selected_thresholds.max_quality_drop:
                findings.append(
                    f"{metric} dropped by {-comparison['delta']:.6f}"
                )
            elif comparison["delta"] > 0.0:
                improved = True
        for metric in _COUNT_METRICS:
            comparison = _metric_delta(baseline, candidate, metric)
            metrics[metric] = comparison
            if comparison["delta"] > selected_thresholds.max_count_increase:
                findings.append(f"{metric} increased by {comparison['delta']:.0f}")
            elif comparison["delta"] < 0.0:
                improved = True

        timings = {
            metric: _metric_delta(baseline, candidate, metric)
            for metric in _TIMING_METRICS
            if metric in baseline and metric in candidate
        }
        rows.append(
            {
                "configuration": _configuration(identity),
                "status": (
                    "regressed"
                    if findings
                    else "improved"
                    if improved
                    else "unchanged"
                ),
                "findings": findings,
                "metrics": metrics,
                "timings": timings,
            }
        )

    for identity in sorted(set(candidate_rows) - set(baseline_rows)):
        rows.append(
            {
                "configuration": _configuration(identity),
                "status": "new",
                "findings": ["configuration is new in candidate"],
                "metrics": {},
                "timings": {},
            }
        )

    counts = {
        status: sum(row["status"] == status for row in rows)
        for status in ("regressed", "missing", "improved", "unchanged", "new")
    }
    failed = bool(global_findings or counts["regressed"] or counts["missing"])
    return {
        "schema_version": 1,
        "data_classification": "synthetic",
        "status": "failed" if failed else "passed",
        "baseline_file_name": Path(baseline_file_name).name,
        "candidate_file_name": Path(candidate_file_name).name,
        "input_fingerprints_match": fingerprints_match,
        "allow_input_change": allow_input_change,
        "thresholds": {
            "max_quality_drop": selected_thresholds.max_quality_drop,
            "max_count_increase": selected_thresholds.max_count_increase,
        },
        "global_findings": global_findings,
        "counts": counts,
        "rows": rows,
    }


def run_regression_comparison(
    lab_root: str | Path,
    *,
    baseline_path: str | Path,
    candidate_path: str | Path,
    output_name: str = "rag-regression",
    max_quality_drop: float = 0.0,
    max_count_increase: int = 0,
    allow_input_change: bool = False,
) -> RegressionRunOutput:
    paths = LabPaths.create(lab_root)
    output_json_name = f"{output_name}.json"
    input_names = {
        Path(baseline_path).name.casefold(),
        Path(candidate_path).name.casefold(),
    }
    if Path(output_json_name).name.casefold() in input_names:
        raise ValueError("regression output must not overwrite an input report")
    try:
        baseline = read_generated_json(paths, baseline_path)
    except PathValidationError as generated_error:
        baseline_paths = LabPaths.create(lab_root, input_roots=("baselines",))
        try:
            baseline = read_json(baseline_paths, baseline_path)
            validate_tracked_baseline(baseline)
        except PathValidationError:
            raise generated_error
    candidate = read_generated_json(paths, candidate_path)
    report = compare_reports(
        baseline,
        candidate,
        baseline_file_name=Path(baseline_path).name,
        candidate_file_name=Path(candidate_path).name,
        thresholds=RegressionThresholds(
            max_quality_drop=max_quality_drop,
            max_count_increase=max_count_increase,
        ),
        allow_input_change=allow_input_change,
    )
    files = write_regression_reports(paths, output_name, report)
    return RegressionRunOutput(report=report, files=files)
