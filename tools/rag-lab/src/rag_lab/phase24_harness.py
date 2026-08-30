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
