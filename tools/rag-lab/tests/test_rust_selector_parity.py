from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess

import pytest

from rag_lab.coverage_set_selection import select_coverage_evidence
from test_phase18_coverage_selection import CASES, _request


RUST_EXE = os.environ.get("RAG_SELECTOR_RS_EXE", "")


@pytest.mark.skipif(not RUST_EXE or not Path(RUST_EXE).is_file(), reason="Rust CLI not built")
@pytest.mark.parametrize("case", CASES, ids=lambda case: case["id"])
def test_rust_cli_matches_python_reference(case: dict[str, object]):
    request = _request(case)
    python_result = select_coverage_evidence(request)
    completed = subprocess.run(
        [RUST_EXE],
        input=json.dumps(_rust_request(case), ensure_ascii=False),
        text=True,
        encoding="utf-8",
        capture_output=True,
        timeout=2,
        check=False,
    )

    assert completed.returncode == 0, completed.stderr
    rust = json.loads(completed.stdout)
    assert rust["selectedEvidenceIds"] == [item.candidate_id for item in python_result.selected]
    assert rust["selectedCoverage"] == list(python_result.selected_coverage)
    assert rust["missingCoverage"] == list(python_result.missing_coverage)
    assert rust["selectedEvidenceCount"] == len(python_result.selected)
    assert rust["budgetLimited"] == python_result.budget_limited


def _rust_request(case: dict[str, object]) -> dict[str, object]:
    candidates = []
    for index, item in enumerate(case["candidates"], start=1):
        quality = float(item.get("quality", 0.0))
        candidates.append(
            {
                "candidateId": item["id"],
                "originalRank": int(item.get("rank", index)),
                "sourceType": item.get("sourceType", "Manual"),
                "documentId": item.get("documentId"),
                "filePath": item.get("filePath"),
                "section": item.get("section"),
                "text": item.get("text", item["id"]),
                "contentHash": item.get("contentHash"),
                "technicalTokens": item.get("technicalTokens", []),
                "coverage": item.get("coverage", []),
                "rankingScore": quality,
                "topicScore": quality,
                "entityScore": quality,
                "technicalTokenScore": quality,
                "sourceTrust": quality,
                "versionScore": quality,
                "explicitlyExcluded": bool(item.get("explicitlyExcluded", False)),
                "topicConflict": bool(item.get("topicConflict", False)),
                "productMismatch": bool(item.get("productMismatch", False)),
                "isManuallySelected": bool(item.get("manuallySelected", False)),
                "estimatedChars": int(item.get("estimatedChars", 0)),
            }
        )
    return {
        "requiredCoverage": case["requiredCoverage"],
        "candidates": candidates,
        "baseMaxItems": int(case.get("baseMaxItems", 3)),
        "expansionMaxItems": int(case.get("expansionMaxItems", 5)),
        "characterBudget": int(case.get("characterBudget", 2000)),
        "minimumQualityScore": float(case.get("minimumQualityScore", 0.30)),
    }
