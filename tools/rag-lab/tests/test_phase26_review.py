import json
from pathlib import Path

from rag_lab.phase26_review import build_review, protected_values


def test_phase26_excludes_klocwork_from_quality_denominator(tmp_path):
    cases_path = Path(__file__).parents[1] / "phase24-2-answer-quality-cases.json"
    rows = []
    cases = json.loads(open(cases_path, encoding="utf-8").read())
    for case in cases:
        rows.append({
            "CaseId": case["id"], "Product": case["product"], "Readiness": "InsufficientEvidence" if case["product"] == "Klocwork" else "CustomerReady",
            "TechnicalQuery": case["question"], "DeterministicAnswer": "回答", "Evidence": [],
            "SafetyPass": True, "ReferenceAvailable": 0, "ReferenceDisplayed": 0,
        })
    report = tmp_path / "live.json"
    report.write_text(json.dumps({"Cases": rows}), encoding="utf-8")
    result = build_review(report, cases_path)
    assert result["Aggregate"]["CaseCount"] == 12
    assert len(result["Klocwork"]["Cases"]) == 4


def test_protected_values_cannot_be_removed():
    before = "qacli validate build --retry 2 Page 12 https://example.invalid/doc"
    assert protected_values(before, before + " 整理しました")
    assert not protected_values(before, "手順を整理しました")
