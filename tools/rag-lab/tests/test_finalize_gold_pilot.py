from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from finalize_gold_pilot import PILOT_IDS, approval_findings, evaluate, selected_cases, shadow_findings


def test_fixed_pilot_rejects_holdout_substitution() -> None:
    rows = [{"caseId": case_id, "product": ("QAC" if case_id.startswith("qac") else
             "CHECKMARX" if case_id.startswith("checkmarx") else "KLOCWORK")}
            for case_id in PILOT_IDS]
    candidates = {row["caseId"]: {"product": row["product"], "split": "development"} for row in rows}
    candidates["klocwork-03"]["split"] = "holdout"
    with pytest.raises(ValueError, match="開発用区分"):
        selected_cases({"cases": rows, "holdoutUsed": False, "shadowComparisonExecuted": True,
                        "formalQualityComparisonExecuted": False}, candidates)


def test_shadow_comparison_does_not_count_as_formal_comparison() -> None:
    rows = [{"caseId": case_id, "product": ("QAC" if case_id.startswith("qac") else
             "CHECKMARX" if case_id.startswith("checkmarx") else "KLOCWORK")}
            for case_id in PILOT_IDS]
    candidates = {row["caseId"]: {"product": row["product"], "split": "development"} for row in rows}
    selection = {"cases": rows, "holdoutUsed": False, "shadowComparisonExecuted": True,
                 "formalQualityComparisonExecuted": False}
    assert len(selected_cases(selection, candidates)) == 8
    selection["formalQualityComparisonExecuted"] = True
    with pytest.raises(ValueError, match="品質比較フラグ"):
        selected_cases(selection, candidates)


def test_codex_review_and_unresolved_evidence_cannot_approve_answer() -> None:
    row = {"J": "顧客相談TXT SHA256:abc\n追加で必要: メーカー回答",
           "K": "NeedsReview", "M": "Codex一次監査", "N": "2026-09-27"}
    approval = {"decision": "approved_answer", "reviewer": "Codex一次監査",
                "reviewDate": "2026-09-27", "expectedReadiness": "NeedsReview",
                "basisLocators": ["顧客相談TXT SHA256:abc"]}
    findings = approval_findings("qac-01", row, approval, {"qac-01"})
    assert "human_reviewer_mismatch" in findings
    assert "answer_evidence_unresolved" in findings


def test_shadow_accepts_reviewed_evidence_gap_without_human_approval() -> None:
    row = {"F": "Codex一次確認済", "G": "Codex一次確認済", "H": "顧客は仕様を確認したい",
           "I": "正式対応を断定しない", "J": "顧客相談TXT SHA256:abc\nEvidence状態: 不足",
           "K": "InsufficientEvidence", "M": "Codex一次監査", "N": "2026-09-27"}
    firstpass = {"caseId": "qac-01", "split": "development", **row,
                 "unreviewedMaterial": False, "conflict": False, "evidenceSufficient": False}
    assert shadow_findings("qac-01", row, firstpass) == []
    row["H"] = ""
    assert "expected_claims_missing" in shadow_findings("qac-01", row, firstpass)
    row["H"] = firstpass["H"]
    row["K"] = "CustomerReady"
    assert "shadow_readiness_invalid" in shadow_findings("qac-01", row, firstpass)
    firstpass["unreviewedMaterial"] = True
    assert "codex_evidence_review_incomplete" in shadow_findings("qac-01", row, firstpass)


def test_pilot_needs_six_supported_answers_across_all_products() -> None:
    rows = [{"caseId": case_id, "product": ("QAC" if case_id.startswith("qac") else
             "CHECKMARX" if case_id.startswith("checkmarx") else "KLOCWORK")}
            for case_id in PILOT_IDS]
    workbook_rows = {case_id: {"J": "公式資料の節 3.2", "K": "NeedsReview", "M": "担当者", "N": "2026-09-27"}
                     for case_id in PILOT_IDS}
    approvals = {"cases": [{"caseId": case_id, "decision": "approved_answer", "reviewer": "担当者",
                             "reviewDate": "2026-09-27", "expectedReadiness": "NeedsReview",
                             "basisLocators": ["公式資料の節 3.2"]} for case_id in PILOT_IDS]}
    assert evaluate(rows, workbook_rows, set(PILOT_IDS), approvals)["formalGoldReady"]
    for entry in approvals["cases"][:3]:
        entry["decision"] = "approved_abstention"
        entry["expectedReadiness"] = "InsufficientEvidence"
        workbook_rows[entry["caseId"]]["K"] = "InsufficientEvidence"
    status = evaluate(rows, workbook_rows, set(PILOT_IDS), approvals)
    assert status["approvedCount"] == 8
    assert status["approvedAnswerCount"] == 5
    assert not status["formalGoldReady"]
