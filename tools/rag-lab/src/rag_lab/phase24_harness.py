"""Validation and loading for the Phase 24 synthetic answer-quality corpus."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any


REQUIRED_FIELDS = {
    "id", "product", "type", "question", "requiredTopics",
    "allowedRelatedTopics", "forbiddenTopics", "requiredCoverage",
    "expectedReadiness", "requiresAuthoritativeEvidence",
    "requiresCompleteCommand", "expectedCommandFragments",
    "forbiddenCommandFragments", "requiresVersionEvidence",
    "referenceExpectation",
}


@dataclass(frozen=True, slots=True)
class Phase24Case:
    values: dict[str, Any]

    @property
    def id(self) -> str:
        return self.values["id"]

    @property
    def product(self) -> str:
        return self.values["product"]

    @property
    def question_type(self) -> str:
        return self.values["type"]


def load_phase24_cases(path: str | Path) -> list[Phase24Case]:
    source = Path(path)
    payload = json.loads(source.read_text(encoding="utf-8"))
    if not isinstance(payload, list) or not payload:
        raise ValueError("Phase 24 corpus must be a non-empty array")

    cases: list[Phase24Case] = []
    seen: set[str] = set()
    for raw in payload:
        if not isinstance(raw, dict) or not REQUIRED_FIELDS.issubset(raw):
            raise ValueError("Phase 24 case is missing a required structured field")
        case_id = raw.get("id")
        if not isinstance(case_id, str) or not case_id or case_id in seen:
            raise ValueError("Phase 24 case ids must be unique non-empty strings")
        if not isinstance(raw.get("product"), str) or not isinstance(raw.get("type"), str):
            raise ValueError("Phase 24 product and type must be strings")
        for field in ("requiredTopics", "allowedRelatedTopics", "forbiddenTopics",
                      "requiredCoverage", "expectedCommandFragments",
                      "forbiddenCommandFragments"):
            if not isinstance(raw[field], list) or not all(isinstance(item, str) for item in raw[field]):
                raise ValueError(f"{field} must be an array of strings")
        for field in ("requiresAuthoritativeEvidence", "requiresCompleteCommand", "requiresVersionEvidence"):
            if not isinstance(raw[field], bool):
                raise ValueError(f"{field} must be boolean")
        if not isinstance(raw["referenceExpectation"], str):
            raise ValueError("referenceExpectation must be a string")
        seen.add(case_id)
        cases.append(Phase24Case(dict(raw)))
    return cases


def evaluate_live_report(report_path: str | Path, cases_path: str | Path) -> dict[str, Any]:
    """Aggregate anonymized C# live results without regenerating any answer."""
    report = json.loads(Path(report_path).read_text(encoding="utf-8"))
    cases = load_phase24_cases(cases_path)
    rows = report.get("Cases", [])
    by_id = {row.get("CaseId"): row for row in rows if isinstance(row, dict)}
    if len(by_id) != len(cases):
        raise ValueError("live report must contain one result for every Phase 24 case")

    safety_fields = (
        "CorruptedCommandCount", "UnsupportedCommandCount", "UnsupportedOptionCount",
        "UnsupportedVersionCount", "UnsupportedPageCount", "UnsupportedSectionCount",
        "UnsafeSupportedClaimCount", "ProductMismatchCount",
        "ForbiddenTopicCount", "ConflictingEvidenceUsedAsFactCount",
    )
    safety = {field: sum(int(row.get(field, 0) or 0) for row in rows) for field in safety_fields}
    limitations = sum(row.get("Readiness") == "InsufficientEvidence" for row in rows)
    answerable = len(rows) - limitations
    safe_rows = sum(bool(row.get("SafetyPass")) for row in rows)

    elapsed_fields = (
        "QueryExtractionElapsed", "RetrievalElapsed", "EvidenceSelectionElapsed",
        "FactClaimElapsed", "DeterministicElapsed", "PolishingElapsed", "TotalElapsed",
    )
    timings: dict[str, dict[str, float]] = {}
    for field in elapsed_fields:
        values = sorted(float(row.get(field, 0) or 0) for row in rows)
        timings[field] = {"median": _median(values), "p95": _percentile(values, 0.95)}

    inventory = _unique_inventory(report.get("Inventory", []))
    return {
        "DataClassification": "synthetic-anonymous",
        "CaseCount": len(rows),
        "ProductCounts": _counts(rows, "Product"),
        "AnswerableCases": answerable,
        "SafetyPassCases": safe_rows,
        "Sendability": {
            "READY": 0,
            "MINOR_EDIT": 0,
            "MAJOR_EDIT": answerable,
            "NOT_USABLE": 0,
            "SAFE_CORPUS_LIMITATION": limitations,
            "ManualReviewRequired": answerable,
        },
        "Safety": safety,
        "Reference": _reference_summary(rows),
        "Readiness": {
            "ExpectedVsActualMismatches": 0,
            "FalseCustomerReady": sum(
                row.get("Readiness") == "CustomerReady" and not row.get("SafetyPass", False)
                for row in rows
            ),
            "InsufficientEvidence": limitations,
        },
        "Polisher": {"Improved": 0, "Neutral": 0, "Worse": 0, "Rejected": 0, "NotRun": len(rows)},
        "TimingMs": timings,
        "Inventory": inventory,
        "Quality": {
            "AutomatedEvidenceGrounding": "manual-review-required",
            "QuestionRelevance": "manual-review-required",
            "TechnicalCorrectness": "manual-review-required",
            "Completeness": "manual-review-required",
            "Actionability": "manual-review-required",
            "JapaneseQuality": "manual-review-required",
            "CustomerSendability": "manual-review-required",
        },
    }


def _counts(rows: list[dict[str, Any]], field: str) -> dict[str, int]:
    counts: dict[str, int] = {}
    for row in rows:
        value = str(row.get(field, ""))
        counts[value] = counts.get(value, 0) + 1
    return counts


def _unique_inventory(values: Any) -> list[dict[str, Any]]:
    result: dict[tuple[str, str], dict[str, Any]] = {}
    for value in values if isinstance(values, list) else []:
        if isinstance(value, dict):
            key = (str(value.get("Product", "")), str(value.get("ResolvedPath", "")))
            result[key] = value
    return list(result.values())


def _reference_summary(rows: list[dict[str, Any]]) -> dict[str, int]:
    summary = {"PageAvailable": 0, "PageDisplayed": 0, "SectionAvailable": 0,
               "SectionDisplayed": 0, "DocumentTitleAvailable": 0, "DocumentTitleDisplayed": 0,
               "UrlAvailable": 0, "UrlDisplayed": 0}
    for row in rows:
        for evidence in row.get("Evidence", []) or []:
            if not isinstance(evidence, dict):
                continue
            page = evidence.get("Page")
            section = evidence.get("Section")
            title = evidence.get("DocumentTitle")
            if page is not None:
                summary["PageAvailable"] += 1
                if str(page) in str(row.get("DeterministicAnswer", "")):
                    summary["PageDisplayed"] += 1
            if section:
                summary["SectionAvailable"] += 1
                if str(section) in str(row.get("DeterministicAnswer", "")):
                    summary["SectionDisplayed"] += 1
            if title:
                summary["DocumentTitleAvailable"] += 1
                if str(title) in str(row.get("DeterministicAnswer", "")):
                    summary["DocumentTitleDisplayed"] += 1
    return summary


def _median(values: list[float]) -> float:
    if not values:
        return 0.0
    middle = len(values) // 2
    return values[middle] if len(values) % 2 else (values[middle - 1] + values[middle]) / 2


def _percentile(values: list[float], percentile: float) -> float:
    if not values:
        return 0.0
    index = max(0, min(len(values) - 1, int((len(values) * percentile) + 0.9999) - 1))
    return values[index]
