from collections import Counter
import json
from pathlib import Path

from rag_lab.phase24_harness import evaluate_live_report, load_phase24_cases


def test_phase24_corpus_is_structured_and_balanced() -> None:
    path = Path(__file__).parents[1] / "phase24-2-answer-quality-cases.json"
    cases = load_phase24_cases(path)

    assert len(cases) == 16
    assert Counter(case.product for case in cases) == {
        "HelixQAC": 4,
        "Checkmarx One": 4,
        "Validate": 4,
        "Klocwork": 4,
    }
    assert Counter(case.question_type for case in cases)["HowTo"] >= 1
    assert all(case.values["requiredCoverage"] for case in cases)


def test_live_report_aggregation_keeps_corpus_limitations_separate(tmp_path):
    cases_path = Path(__file__).parents[1] / "phase24-2-answer-quality-cases.json"
    rows = []
    for case in load_phase24_cases(cases_path):
        limited = case.product == "Klocwork"
        rows.append({
            "CaseId": case.id,
            "Product": case.product,
            "Readiness": "InsufficientEvidence" if limited else "CustomerReady",
            "SafetyPass": True,
            "Evidence": [],
            "TotalElapsed": 10,
        })
    report = tmp_path / "live.json"
    report.write_text(json.dumps({"Cases": rows, "Inventory": []}), encoding="utf-8")

    result = evaluate_live_report(report, cases_path)

    assert result["CaseCount"] == 16
    assert result["Sendability"]["SAFE_CORPUS_LIMITATION"] == 4
    assert result["Sendability"]["MAJOR_EDIT"] == 12
    assert result["Safety"]["CorruptedCommandCount"] == 0
