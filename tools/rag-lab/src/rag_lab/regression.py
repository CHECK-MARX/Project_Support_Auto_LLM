from __future__ import annotations

from dataclasses import dataclass
import math
from pathlib import Path
from typing import Any, Mapping

from .io import DataValidationError, LabPaths, read_generated_json
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
    baseline = read_generated_json(paths, baseline_path)
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
