from __future__ import annotations

import json
from pathlib import Path

from rag_lab.models import EvaluationCase
from rag_lab.query_privacy import sensitive_query_categories
from rag_lab.runner import _phase22_partition


def test_phase22_golden_set_has_fixed_anonymous_development_and_holdout_split() -> None:
    root = Path(__file__).resolve().parents[1]
    manifest = json.loads(
        (root / "samples" / "phase22_golden_manifest.json").read_text(encoding="utf-8")
    )
    payload = json.loads(
        (root / "samples" / "phase22_evaluation_cases.json").read_text(encoding="utf-8")
    )
    cases = [EvaluationCase.from_dict(item) for item in payload["evaluation_cases"]]

    assert manifest["dataClassification"] == "synthetic"
    assert len(cases) == 50
    assert sum(_phase22_partition(case) == "development" for case in cases) == 30
    assert sum(_phase22_partition(case) == "holdout" for case in cases) == 20
    assert all(not sensitive_query_categories(case.query) for case in cases)
    assert manifest["stableEvidenceLocatorFields"] == [
        "product",
        "document_title",
        "page",
        "section",
        "content_hash",
        "source_type",
    ]
