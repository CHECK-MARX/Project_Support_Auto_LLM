from __future__ import annotations

import csv
import io
import re
from pathlib import Path
from typing import Any

from .io import LabPaths, write_json_report, write_text_report


_SAFE_REPORT_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$")
_SUMMARY_COLUMNS = (
    "chunk_strategy",
    "search_method",
    "reranker",
    "filter_mode",
    "top_k",
    "document_count",
    "chunk_count",
    "index_build_time_ms",
    "query_count",
    "mean_precision_at_k",
    "mean_recall_at_k",
    "mrr",
    "mean_ndcg_at_k",
    "mean_search_time_ms",
    "mean_required_term_coverage_at_k",
    "excluded_term_hit_count",
    "product_confusion_count",
    "version_mismatch_count",
    "quality_gate_passed",
)


def _validate_report_name(report_name: str) -> None:
    if not _SAFE_REPORT_NAME.fullmatch(report_name):
        raise ValueError(
            "report name must contain only letters, numbers, period, underscore, or hyphen"
        )


def _csv_text(rows: list[dict[str, Any]]) -> str:
    stream = io.StringIO(newline="")
    writer = csv.DictWriter(stream, fieldnames=_SUMMARY_COLUMNS, extrasaction="ignore")
    writer.writeheader()
    writer.writerows(rows)
    return stream.getvalue()


def _display(value: Any) -> str:
    if isinstance(value, float):
        return f"{value:.6f}"
    return str(value)


def _markdown_text(rows: list[dict[str, Any]], report_name: str) -> str:
    compact_columns = (
        "chunk_strategy",
        "search_method",
        "reranker",
        "filter_mode",
        "top_k",
        "mean_precision_at_k",
        "mean_recall_at_k",
        "mrr",
        "mean_ndcg_at_k",
        "mean_search_time_ms",
        "mean_required_term_coverage_at_k",
        "excluded_term_hit_count",
        "product_confusion_count",
        "version_mismatch_count",
        "quality_gate_passed",
    )
    lines = [
        f"# RAG evaluation: {report_name}",
        "",
        "Synthetic/offline evaluation. No source absolute paths are included.",
        "",
        "| " + " | ".join(compact_columns) + " |",
        "| " + " | ".join("---" for _ in compact_columns) + " |",
    ]
    lines.extend(
        "| "
        + " | ".join(_display(row.get(column, "")) for column in compact_columns)
        + " |"
        for row in rows
    )
    return "\n".join(lines)


def write_comparison_reports(
    paths: LabPaths, report_name: str, report: dict[str, Any]
) -> dict[str, Path]:
    _validate_report_name(report_name)
    summary = report.get("summary")
    if not isinstance(summary, list) or not all(isinstance(row, dict) for row in summary):
        raise ValueError("report summary must be an array of objects")
    return {
        "json": write_json_report(paths, f"{report_name}.json", report),
        "csv": write_text_report(paths, f"{report_name}.csv", _csv_text(summary)),
        "markdown": write_text_report(
            paths,
            f"{report_name}.md",
            _markdown_text(summary, report_name),
        ),
    }


def _regression_markdown(report: dict[str, Any], report_name: str) -> str:
    counts = report["counts"]
    lines = [
        f"# RAG regression comparison: {report_name}",
        "",
        "Synthetic/offline comparison. Source paths and document text are omitted.",
        "",
        f"- Status: {report['status']}",
        f"- Baseline: {report['baseline_file_name']}",
        f"- Candidate: {report['candidate_file_name']}",
        f"- Input fingerprints match: {report['input_fingerprints_match']}",
        f"- Regressed configurations: {counts['regressed']}",
        f"- Missing configurations: {counts['missing']}",
        f"- Improved configurations: {counts['improved']}",
        f"- Unchanged configurations: {counts['unchanged']}",
        f"- New configurations: {counts['new']}",
    ]
    lines.extend(
        f"- Finding: {finding}" for finding in report.get("global_findings", ())
    )
    lines.extend(
        (
            "",
            "Performance timing deltas are informational and do not affect status.",
            "",
            "| status | chunk_strategy | search_method | reranker | filter_mode | top_k | findings |",
            "| --- | --- | --- | --- | --- | --- | --- |",
        )
    )
    for row in report["rows"]:
        configuration = row["configuration"]
        findings = "; ".join(row.get("findings", ())).replace("|", "\\|")
        lines.append(
            "| "
            + " | ".join(
                (
                    str(row["status"]),
                    str(configuration["chunk_strategy"]),
                    str(configuration["search_method"]),
                    str(configuration["reranker"]),
                    str(configuration["filter_mode"]),
                    str(configuration["top_k"]),
                    findings,
                )
            )
            + " |"
        )
    return "\n".join(lines)


def write_regression_reports(
    paths: LabPaths, report_name: str, report: dict[str, Any]
) -> dict[str, Path]:
    _validate_report_name(report_name)
    rows = report.get("rows")
    if not isinstance(rows, list) or not all(isinstance(row, dict) for row in rows):
        raise ValueError("regression report rows must be an array of objects")
    return {
        "json": write_json_report(paths, f"{report_name}.json", report),
        "markdown": write_text_report(
            paths,
            f"{report_name}.md",
            _regression_markdown(report, report_name),
        ),
    }
