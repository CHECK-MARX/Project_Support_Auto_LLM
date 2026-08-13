from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
import hashlib
from pathlib import Path
import re
from typing import Any, Mapping

from .answer_quality import (
    BLOCKED,
    CUSTOMER_READY,
    INSUFFICIENT_EVIDENCE,
    NEEDS_REVIEW,
    AnswerQualityEvidence,
    AnswerQualityInput,
    create_support_catalog,
    evaluate_answer_quality,
)
from .io import DataValidationError, LabPaths, read_json, write_json_report, write_text_report


_PHASES = ("phase14", "phase15", "phase16")
_DECISIONS = (CUSTOMER_READY, NEEDS_REVIEW, INSUFFICIENT_EVIDENCE, BLOCKED)
_SAFE_OUTPUT_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,79}")
_METRIC_KEYS = (
    "directness",
    "grounding",
    "topicAlignment",
    "coverage",
    "technicalFidelity",
    "unsupportedClaimCount",
    "conflictCount",
    "actionability",
    "customerReadiness",
    "internalLeakageCount",
    "decision",
)


@dataclass(frozen=True, slots=True)
class AnswerQualityComparisonOutput:
    report: dict[str, object]
    files: dict[str, Path]


def run_answer_quality_comparison(
    lab_root: str | Path,
    *,
    cases_path: str | Path = "samples/phase17_answer_quality_cases.json",
    output_name: str = "phase17-answer-quality",
) -> AnswerQualityComparisonOutput:
    if not _SAFE_OUTPUT_NAME.fullmatch(output_name):
        raise ValueError("answer quality output name contains unsupported characters")

    paths = LabPaths.create(lab_root, input_roots=("samples",))
    source_path = paths.resolve_input(cases_path)
    cases = _validated_cases(read_json(paths, cases_path))
    rows: list[dict[str, object]] = []
    phase_results: dict[str, list[dict[str, object]]] = {
        phase: [] for phase in (*_PHASES, "phase17")
    }
    false_positive_count = 0
    false_negative_count = 0
    mismatches: list[str] = []

    for case in cases:
        evidence = tuple(
            AnswerQualityEvidence(
                source_id=item["sourceId"],
                source_type=item["sourceType"],
                text=item["text"],
                product_name=case["product"],
                version=item.get("version"),
            )
            for item in case["evidence"]
        )
        evaluated: dict[str, dict[str, object]] = {}
        for phase in _PHASES:
            result = evaluate_answer_quality(
                AnswerQualityInput(
                    question=case["question"],
                    answer=case["answers"][phase],
                    evidence=evidence,
                    product_name=case["product"],
                    catalog=create_support_catalog(case["product"]),
                )
            )
            evaluated[phase] = _safe_metrics(result)
            phase_results[phase].append(result)

        phase17 = dict(evaluated["phase16"])
        phase17["gateApplied"] = True
        evaluated["phase17"] = phase17
        phase_results["phase17"].append(dict(phase17))

        expected = case["expectedDecision"]
        actual = str(phase17["decision"])
        expected_unsupported = case["expectedUnsupportedClaimCount"]
        actual_unsupported = int(phase17["unsupportedClaimCount"])
        if actual != expected or actual_unsupported != expected_unsupported:
            mismatches.append(case["id"])
        if expected != CUSTOMER_READY and actual == CUSTOMER_READY:
            false_positive_count += 1
        if expected == CUSTOMER_READY and actual == BLOCKED:
            false_negative_count += 1

        rows.append(
            {
                "caseId": case["id"],
                "evidenceTopK": case["evidenceTopK"],
                "phases": evaluated,
                "expectedDecision": expected,
                "expectedUnsupportedClaimCount": expected_unsupported,
                "matchesExpected": (
                    actual == expected and actual_unsupported == expected_unsupported
                ),
            }
        )

    aggregate = {
        phase: _aggregate(results)
        for phase, results in phase_results.items()
    }
    status = (
        "passed"
        if not mismatches and false_positive_count == 0 and false_negative_count == 0
        else "failed"
    )
    source_bytes = source_path.read_bytes().replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    report: dict[str, object] = {
        "schemaVersion": 1,
        "dataClassification": "synthetic",
        "containsRawQuestionAnswerOrEvidence": False,
        "input": {
            "fileName": source_path.name,
            "sha256": hashlib.sha256(source_bytes).hexdigest(),
            "caseCount": len(cases),
        },
        "phaseDefinitions": {
            "phase14": "legacy evidence-connected answer",
            "phase15": "question-aware ranked answer",
            "phase16": "topic/entity reranked answer",
            "phase17": "phase16 answer evaluated by deterministic production-readiness gate",
        },
        "cases": rows,
        "aggregate": aggregate,
        "qualityGate": {
            "status": status,
            "falsePositiveCount": false_positive_count,
            "falseNegativeCount": false_negative_count,
            "mismatchCount": len(mismatches),
            "mismatchCaseIds": mismatches,
            "policy": "False positives are blocking; safe NeedsReview outcomes are preferred.",
        },
    }
    files = {
        "json": write_json_report(paths, f"{output_name}.json", report),
        "markdown": write_text_report(paths, f"{output_name}.md", _markdown(report)),
    }
    return AnswerQualityComparisonOutput(report=report, files=files)


def _validated_cases(raw: object) -> list[dict[str, Any]]:
    if not isinstance(raw, Mapping) or raw.get("dataClassification") != "synthetic":
        raise DataValidationError("answer quality input must be classified as synthetic")
    records = raw.get("cases")
    if not isinstance(records, list) or not records:
        raise DataValidationError("answer quality input must contain a non-empty cases array")

    validated: list[dict[str, Any]] = []
    for item in records:
        if not isinstance(item, Mapping):
            raise DataValidationError("answer quality case must be an object")
        required_strings = ("id", "product", "question", "expectedDecision")
        if any(not isinstance(item.get(key), str) or not item[key] for key in required_strings):
            raise DataValidationError("answer quality case has an invalid required string")
        answers = item.get("answers")
        if not isinstance(answers, Mapping) or any(
            not isinstance(answers.get(phase), str) for phase in _PHASES
        ):
            raise DataValidationError("answer quality case must define Phase 14-16 answers")
        evidence = item.get("evidence")
        evidence_top_k = item.get("evidenceTopK")
        if not isinstance(evidence, list) or not isinstance(evidence_top_k, list):
            raise DataValidationError("answer quality evidence fields must be arrays")
        for evidence_item in evidence:
            if not isinstance(evidence_item, Mapping) or any(
                not isinstance(evidence_item.get(key), str)
                for key in ("sourceId", "sourceType", "text")
            ):
                raise DataValidationError("answer quality evidence item is invalid")
        if item["expectedDecision"] not in _DECISIONS:
            raise DataValidationError("answer quality expected decision is invalid")
        unsupported = item.get("expectedUnsupportedClaimCount")
        if not isinstance(unsupported, int) or isinstance(unsupported, bool) or unsupported < 0:
            raise DataValidationError("expectedUnsupportedClaimCount must be non-negative")
        validated.append(dict(item))
    return validated


def _safe_metrics(result: Mapping[str, object]) -> dict[str, object]:
    return {key: result[key] for key in _METRIC_KEYS}


def _aggregate(results: list[Mapping[str, object]]) -> dict[str, object]:
    count = len(results)
    decisions = Counter(str(item["decision"]) for item in results)
    return {
        "caseCount": count,
        "meanCoverage": _mean(results, "coverage"),
        "meanTopicAlignment": _mean(results, "topicAlignment"),
        "conflictCount": sum(int(item["conflictCount"]) for item in results),
        "unsupportedTechnicalClaimCount": sum(
            int(item["unsupportedClaimCount"]) for item in results
        ),
        "customerReadyRate": _rate(decisions[CUSTOMER_READY], count),
        "needsReviewRate": _rate(decisions[NEEDS_REVIEW], count),
        "insufficientEvidenceRate": _rate(decisions[INSUFFICIENT_EVIDENCE], count),
        "blockedRate": _rate(decisions[BLOCKED], count),
        "decisionCounts": {
            decision: decisions[decision] for decision in _DECISIONS
        },
    }


def _mean(results: list[Mapping[str, object]], key: str) -> float:
    return round(sum(float(item[key]) for item in results) / len(results), 6)


def _rate(value: int, total: int) -> float:
    return round(value / total, 6) if total else 0.0


def _markdown(report: Mapping[str, object]) -> str:
    aggregate = report["aggregate"]
    assert isinstance(aggregate, Mapping)
    gate = report["qualityGate"]
    assert isinstance(gate, Mapping)
    lines = [
        "# Phase 17 Answer Quality Comparison",
        "",
        "Synthetic diagnostics only. Raw questions, answers and evidence are excluded.",
        "",
        "| Phase | Coverage | Topic | Conflicts | Unsupported | CustomerReady | NeedsReview | Insufficient | Blocked |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    for phase in (*_PHASES, "phase17"):
        item = aggregate[phase]
        assert isinstance(item, Mapping)
        lines.append(
            f"| {phase} | {item['meanCoverage']:.3f} | "
            f"{item['meanTopicAlignment']:.3f} | {item['conflictCount']} | "
            f"{item['unsupportedTechnicalClaimCount']} | "
            f"{item['customerReadyRate']:.3f} | {item['needsReviewRate']:.3f} | "
            f"{item['insufficientEvidenceRate']:.3f} | {item['blockedRate']:.3f} |"
        )
    lines.extend(
        [
            "",
            f"Quality gate: **{gate['status']}**",
            f"False positives: {gate['falsePositiveCount']}",
            f"False negatives: {gate['falseNegativeCount']}",
            f"Mismatches: {gate['mismatchCount']}",
        ]
    )
    return "\n".join(lines)
