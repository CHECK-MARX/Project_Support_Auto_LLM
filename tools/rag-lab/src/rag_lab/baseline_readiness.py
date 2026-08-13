from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import re
from typing import Any, Mapping

from .io import LabPaths, read_generated_json, read_json, write_json_report, write_text_report
from .regression import compare_reports, validate_tracked_baseline


_SAFE_OUTPUT_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}\Z")


@dataclass(frozen=True, slots=True)
class ReadinessRunOutput:
    report: dict[str, Any]
    files: dict[str, Path]


def _validate_output_name(output_name: str) -> None:
    if not _SAFE_OUTPUT_NAME.fullmatch(output_name):
        raise ValueError("output name must be a safe simple name")


def _canonical_candidate(candidate: Any) -> tuple[dict[str, Any], str]:
    validate_tracked_baseline(candidate)
    canonical = {
        key: value for key, value in candidate.items() if key != "report_name"
    }
    serialized = json.dumps(
        canonical,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return canonical, hashlib.sha256(serialized).hexdigest()


def compare_candidate_reproducibility(
    first_candidate: Any,
    second_candidate: Any,
    *,
    first_file_name: str,
    second_file_name: str,
) -> dict[str, Any]:
    first_canonical, first_digest = _canonical_candidate(first_candidate)
    second_canonical, second_digest = _canonical_candidate(second_candidate)
    matches = first_canonical == second_canonical
    return {
        "schema_version": 1,
        "data_classification": "synthetic",
        "status": "passed" if matches else "failed",
        "first_file_name": Path(first_file_name).name,
        "second_file_name": Path(second_file_name).name,
        "first_digest": first_digest,
        "second_digest": second_digest,
        "content_match": matches,
        "findings": [] if matches else ["canonical candidate contents differ"],
    }


def _reproducibility_markdown(report: Mapping[str, Any]) -> str:
    findings = report.get("findings", [])
    lines = [
        "# Baseline candidate reproducibility",
        "",
        "Synthetic/offline candidate comparison. Candidate report names are ignored.",
        "",
        f"- Status: {report['status']}",
        f"- First: {report['first_file_name']}",
        f"- Second: {report['second_file_name']}",
        f"- Content match: {report['content_match']}",
        f"- First digest: {report['first_digest']}",
        f"- Second digest: {report['second_digest']}",
    ]
    lines.extend(f"- Finding: {finding}" for finding in findings)
    return "\n".join(lines)


def _write_reports(
    paths: LabPaths,
    output_name: str,
    report: dict[str, Any],
    markdown: str,
) -> dict[str, Path]:
    _validate_output_name(output_name)
    return {
        "json": write_json_report(paths, f"{output_name}.json", report),
        "markdown": write_text_report(paths, f"{output_name}.md", markdown),
    }


def run_candidate_reproducibility_check(
    lab_root: str | Path,
    *,
    first_path: str | Path,
    second_path: str | Path,
    output_name: str = "candidate-reproducibility",
) -> ReadinessRunOutput:
    paths = LabPaths.create(lab_root)
    output_json_name = f"{output_name}.json"
    input_names = {Path(first_path).name.casefold(), Path(second_path).name.casefold()}
    if Path(output_json_name).name.casefold() in input_names:
        raise ValueError("reproducibility output must not overwrite an input candidate")
    first_source = paths.resolve_generated_input(first_path)
    second_source = paths.resolve_generated_input(second_path)
    if first_source == second_source:
        raise ValueError("reproducibility requires two different candidate files")
    first = read_generated_json(paths, first_source)
    second = read_generated_json(paths, second_source)
    report = compare_candidate_reproducibility(
        first,
        second,
        first_file_name=Path(first_path).name,
        second_file_name=Path(second_path).name,
    )
    files = _write_reports(
        paths,
        output_name,
        report,
        _reproducibility_markdown(report),
    )
    return ReadinessRunOutput(report=report, files=files)


def assess_baseline_readiness(
    baseline: Any,
    candidate: Any,
    reproduction: Any,
    *,
    baseline_file_name: str,
    candidate_file_name: str,
    reproduction_file_name: str,
) -> dict[str, Any]:
    validate_tracked_baseline(baseline)
    candidate_reproducibility = compare_candidate_reproducibility(
        candidate,
        reproduction,
        first_file_name=candidate_file_name,
        second_file_name=reproduction_file_name,
    )
    regression = compare_reports(
        baseline,
        candidate,
        baseline_file_name=baseline_file_name,
        candidate_file_name=candidate_file_name,
    )
    ready = (
        regression["status"] == "passed"
        and candidate_reproducibility["status"] == "passed"
    )
    findings = list(regression["global_findings"])
    findings.extend(
        finding
        for row in regression["rows"]
        if row["status"] in {"regressed", "missing"}
        for finding in row["findings"]
    )
    findings.extend(candidate_reproducibility["findings"])
    return {
        "schema_version": 1,
        "data_classification": "synthetic",
        "status": "ready" if ready else "blocked",
        "baseline_file_name": Path(baseline_file_name).name,
        "candidate_file_name": Path(candidate_file_name).name,
        "reproduction_file_name": Path(reproduction_file_name).name,
        "candidate_digest": candidate_reproducibility["first_digest"],
        "reproduction_digest": candidate_reproducibility["second_digest"],
        "reproducible": candidate_reproducibility["content_match"],
        "input_fingerprints_match": regression["input_fingerprints_match"],
        "regression_counts": regression["counts"],
        "findings": findings,
    }


def _readiness_markdown(report: Mapping[str, Any]) -> str:
    counts = report["regression_counts"]
    lines = [
        "# Baseline readiness",
        "",
        "Synthetic/offline readiness assessment. No tracked file is modified.",
        "",
        f"- Status: {report['status']}",
        f"- Baseline: {report['baseline_file_name']}",
        f"- Candidate: {report['candidate_file_name']}",
        f"- Reproduction: {report['reproduction_file_name']}",
        f"- Reproducible: {report['reproducible']}",
        f"- Input fingerprints match: {report['input_fingerprints_match']}",
        f"- Regressed configurations: {counts['regressed']}",
        f"- Missing configurations: {counts['missing']}",
        f"- Improved configurations: {counts['improved']}",
        f"- Candidate digest: {report['candidate_digest']}",
        f"- Reproduction digest: {report['reproduction_digest']}",
    ]
    lines.extend(f"- Finding: {finding}" for finding in report["findings"])
    return "\n".join(lines)


def run_baseline_readiness_assessment(
    lab_root: str | Path,
    *,
    baseline_path: str | Path,
    candidate_path: str | Path,
    reproduction_path: str | Path,
    output_name: str = "baseline-readiness",
) -> ReadinessRunOutput:
    paths = LabPaths.create(lab_root)
    baseline_paths = LabPaths.create(lab_root, input_roots=("baselines",))
    output_json_name = f"{output_name}.json"
    generated_input_names = {
        Path(candidate_path).name.casefold(),
        Path(reproduction_path).name.casefold(),
    }
    if Path(output_json_name).name.casefold() in generated_input_names:
        raise ValueError("readiness output must not overwrite an input candidate")
    candidate_source = paths.resolve_generated_input(candidate_path)
    reproduction_source = paths.resolve_generated_input(reproduction_path)
    if candidate_source == reproduction_source:
        raise ValueError("readiness requires two different candidate files")
    baseline = read_json(baseline_paths, baseline_path)
    candidate = read_generated_json(paths, candidate_source)
    reproduction = read_generated_json(paths, reproduction_source)
    report = assess_baseline_readiness(
        baseline,
        candidate,
        reproduction,
        baseline_file_name=Path(baseline_path).name,
        candidate_file_name=Path(candidate_path).name,
        reproduction_file_name=Path(reproduction_path).name,
    )
    files = _write_reports(paths, output_name, report, _readiness_markdown(report))
    return ReadinessRunOutput(report=report, files=files)
