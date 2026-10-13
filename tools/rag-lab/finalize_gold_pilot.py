"""Prepare the fixed eight-case Gold Pilot and gate its private export.

This is separate from the 60-case golden-set finalizer. It never runs a model or
uses holdout cases. The packet stays in reports/generated because reviewer notes
and evidence locators can contain case information.
"""

from __future__ import annotations

import argparse
import json
from collections import Counter
from pathlib import Path

from finalize_closed_case_review import READINESS, _date, _lines, read_review_workbook, validate


ROOT = Path(__file__).resolve().parent / "reports" / "generated"
PILOT_IDS = (
    "qac-01", "qac-06", "qac-15", "checkmarx-02", "checkmarx-09",
    "checkmarx-15", "klocwork-01", "klocwork-03",
)
PRODUCT_COUNTS = {"QAC": 3, "CHECKMARX": 3, "KLOCWORK": 2}
DECISIONS = {"approved_answer", "approved_abstention"}
ABSTENTION_READINESS = {"InsufficientEvidence", "NeedsManufacturerConfirmation", "Blocked"}
INSUFFICIENT_MARKERS = ("Evidence状態: 不足", "追加で必要:", "未調査資料あり", "矛盾",
                        "メーカー原文の出所は別途確認")
SHADOW_READINESS = {"InsufficientEvidence", "NeedsManufacturerConfirmation", "NeedsReview", "Blocked"}


def selected_cases(selection: dict, candidates: dict[str, dict]) -> list[dict]:
    rows = selection.get("cases", [])
    ids = [item.get("caseId") for item in rows]
    if len(rows) != 8 or set(ids) != set(PILOT_IDS) or len(ids) != len(set(ids)):
        raise ValueError("Gold Pilotの固定8件が変更されています。")
    if selection.get("holdoutUsed") is not False or selection.get("formalQualityComparisonExecuted") is not False:
        raise ValueError("選定記録のHoldout/品質比較フラグが不正です。")
    products = Counter()
    for item in rows:
        case = candidates[item["caseId"]]
        if case["split"] != "development" or item.get("product") != case["product"]:
            raise ValueError(f"開発用区分または製品が不正です: {item['caseId']}")
        products[case["product"]] += 1
    if dict(products) != PRODUCT_COUNTS:
        raise ValueError("Gold Pilotの製品構成が変更されています。")
    return rows


def workbook_pilot_rows(workbook: Path) -> dict[str, dict[str, str]]:
    sheets = read_review_workbook(workbook)
    return {values["A"]: values for _, values in sheets["開発用"]
            if values.get("A") in PILOT_IDS}


def approval_findings(case_id: str, row: dict[str, str], approval: dict | None,
                      reviewed: set[str]) -> list[str]:
    findings = []
    if case_id not in reviewed:
        findings.append("formal_human_review_incomplete")
    if approval is None:
        return findings + ["human_decision_missing"]
    decision = approval.get("decision")
    if decision not in DECISIONS:
        findings.append("human_decision_missing")
    reviewer = str(approval.get("reviewer", "")).strip()
    if not reviewer or reviewer == "Codex一次監査" or reviewer != row.get("M", "").strip():
        findings.append("human_reviewer_mismatch")
    date = _date(str(approval.get("reviewDate", "")).strip())
    if not date or date != _date(row.get("N", "").strip()):
        findings.append("human_review_date_mismatch")
    if approval.get("expectedReadiness") != row.get("K", "").strip():
        findings.append("readiness_mismatch")
    locators = approval.get("basisLocators")
    evidence_lines = {line.strip(" ・\t") for line in row.get("J", "").splitlines() if line.strip()}
    if (not isinstance(locators, list) or not locators or
            any(not isinstance(item, str) or item not in evidence_lines for item in locators)):
        findings.append("evidence_basis_missing_or_changed")
    if decision == "approved_answer" and any(marker in row.get("J", "") for marker in INSUFFICIENT_MARKERS):
        findings.append("answer_evidence_unresolved")
    if decision == "approved_abstention" and row.get("K", "").strip() not in ABSTENTION_READINESS:
        findings.append("abstention_readiness_invalid")
    return findings


def shadow_findings(case_id: str, row: dict[str, str], firstpass: dict | None) -> list[str]:
    findings = []
    if firstpass is None or firstpass.get("caseId") != case_id or firstpass.get("split") != "development":
        return ["codex_evidence_review_missing"]
    if (firstpass.get("F") != "Codex一次確認済" or firstpass.get("G") != "Codex一次確認済" or
            firstpass.get("unreviewedMaterial") or firstpass.get("conflict")):
        findings.append("codex_evidence_review_incomplete")
    if row.get("F", "").strip() not in {"Codex一次確認済", "確認済"}:
        findings.append("privacy_firstpass_missing")
    if row.get("G", "").strip() not in {"Codex一次確認済", "確認済"}:
        findings.append("answer_firstpass_missing")
    if any(row.get(column, "") != firstpass.get(column, "") for column in "HIJK"):
        findings.append("codex_labels_changed_after_review")
    for column, label in (("H", "expected_claims_missing"), ("I", "forbidden_claims_missing"),
                          ("J", "required_evidence_missing")):
        if not _lines(row.get(column, "")):
            findings.append(label)
    readiness = row.get("K", "").strip()
    if readiness not in READINESS or (not firstpass.get("evidenceSufficient") and readiness not in SHADOW_READINESS):
        findings.append("shadow_readiness_invalid")
    return findings


def evaluate(rows: list[dict], workbook_rows: dict[str, dict[str, str]],
             reviewed: set[str], approvals: dict | None, firstpass_rows: dict[str, dict] | None = None) -> dict:
    approval_rows = {} if approvals is None else {item["caseId"]: item for item in approvals.get("cases", [])}
    if approvals is not None and (len(approval_rows) != len(approvals.get("cases", [])) or
                                  set(approval_rows) != set(PILOT_IDS)):
        raise ValueError("承認記録の案件IDは固定8件と一対一で対応させてください。")
    if set(workbook_rows) != set(PILOT_IDS):
        raise ValueError("開発用シートにGold Pilot案件が揃っていません。")
    status = []
    answer_products = set()
    firstpass_rows = firstpass_rows or {}
    for item in rows:
        case_id = item["caseId"]
        approval = approval_rows.get(case_id)
        formal_findings = approval_findings(case_id, workbook_rows[case_id], approval, reviewed)
        shadow_missing = shadow_findings(case_id, workbook_rows[case_id], firstpass_rows.get(case_id))
        if not formal_findings and approval["decision"] == "approved_answer":
            answer_products.add(item["product"])
        status.append({"caseId": case_id, "shadowReady": not shadow_missing,
                       "shadowMissingGates": shadow_missing, "formalReady": not formal_findings,
                       "formalMissingGates": formal_findings})
    answer_count = sum(1 for item in rows if not next(s for s in status if s["caseId"] == item["caseId"])["formalMissingGates"]
                       and approval_rows[item["caseId"]]["decision"] == "approved_answer")
    formal_ready = (all(item["formalReady"] for item in status) and answer_count >= 6 and
                    answer_products == set(PRODUCT_COUNTS))
    shadow_ready = all(item["shadowReady"] for item in status)
    return {"schemaVersion": 2, "selectedCount": 8, "approvedCount": sum(item["formalReady"] for item in status),
            "approvedAnswerCount": answer_count, "requiredAnswerCount": 6,
            "answerProductCoverage": sorted(answer_products), "shadowPilotReady": shadow_ready,
            "formalGoldReady": formal_ready, "stage5Ready": formal_ready,
            "holdoutUsed": False, "shadowComparisonExecuted": False,
            "formalQualityComparisonExecuted": False, "cases": status}


def packet_markdown(rows: list[dict], workbook_rows: dict[str, dict[str, str]]) -> str:
    lines = ["# Gold Pilot 一次Evidenceレビュー（固定8件）", "",
             "開発用40件から選定した8件だけを対象とする。未使用評価用20件は使用しない。",
             "以下はCodex一次監査であり、Human Approvedではない。送信証跡は技術的な正しさの証明ではない。",
             "各案件で、根拠の出所、主張の可否、追加証跡、期待Readinessを人が照合する。", ""]
    for item in rows:
        row = workbook_rows[item["caseId"]]
        lines.extend([f"## {item['caseId']} — {item['scope']}", "",
                      f"- 製品: {item['product']}",
                      f"- 一次監査のEvidence状態: {item['evidenceState']}",
                      f"- 暫定Readiness: {row.get('K', '')}",
                      f"- 匿名化・過去返信の一次確認: F={row.get('F', '')}; G={row.get('G', '')}",
                      f"- 送信証跡: {row.get('L', '')}", "",
                      "### 期待する主張（レビュー表H）", "", row.get("H", ""), "",
                      "### 禁止する主張（レビュー表I）", "", row.get("I", ""), "",
                      "### 確認済み資料と不足根拠（レビュー表J）", "", row.get("J", ""), "",
                      "人の判定: 回答ラベルを承認するか、根拠不足による回答保留を承認するか。"
                      " メーカー原文未確認の記述をメーカーの確定回答として扱わない。", ""])
    return "\n".join(lines) + "\n"


def approval_template(rows: list[dict], candidate_sha: str, workbook_sha: str) -> dict:
    return {"schemaVersion": 1, "candidateSetSha256": candidate_sha, "workbookSha256": workbook_sha,
            "cases": [{"caseId": item["caseId"], "decision": "", "reviewer": "",
                       "reviewDate": "", "expectedReadiness": "", "basisLocators": []} for item in rows]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", type=Path, default=ROOT / "closed-case-candidates.json")
    parser.add_argument("--audit", type=Path, default=ROOT / "closed-case-review-audit.json")
    parser.add_argument("--workbook", type=Path, default=ROOT / "outputs" / "01a0dae8-6eac-7081-971a-f8d6aa9a042c" / "closed-case-review.xlsx")
    parser.add_argument("--selection", type=Path, default=ROOT / "closed-case-gold-pilot-selection.json")
    parser.add_argument("--approval", type=Path, default=ROOT / "closed-case-gold-pilot-human-approval.json")
    parser.add_argument("--template", type=Path, default=ROOT / "closed-case-gold-pilot-approval-template.json")
    parser.add_argument("--packet", type=Path, default=ROOT / "closed-case-gold-pilot-packets.md")
    parser.add_argument("--status", type=Path, default=ROOT / "closed-case-gold-pilot-status.json")
    parser.add_argument("--export", type=Path, help="Export the approved eight-case private pilot only when ready")
    args = parser.parse_args()
    candidates = {item["caseId"]: item for item in json.loads(args.candidate.read_text(encoding="utf-8"))["cases"]}
    selection = json.loads(args.selection.read_text(encoding="utf-8"))
    rows = selected_cases(selection, candidates)
    full_status, reviewed = validate(args.candidate, args.audit, args.workbook)
    firstpass = json.loads((ROOT / "closed-case-codex-firstpass.json").read_text(encoding="utf-8"))
    if firstpass.get("humanApproved") != 0 or len(firstpass.get("cases", [])) != 60:
        raise ValueError("一次監査の件数またはHuman Approvedが不正です。")
    firstpass_rows = {item["caseId"]: item for item in firstpass["cases"]}
    if len(firstpass_rows) != 60:
        raise ValueError("一次監査の案件IDが重複しています。")
    workbook_rows = workbook_pilot_rows(args.workbook)
    args.packet.parent.mkdir(parents=True, exist_ok=True)
    args.packet.write_text(packet_markdown(rows, workbook_rows), encoding="utf-8")
    if not args.template.exists():
        args.template.write_text(json.dumps(approval_template(rows, full_status["candidateSetSha256"],
                                                            full_status["workbookSha256"]),
                                            ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    approvals = json.loads(args.approval.read_text(encoding="utf-8")) if args.approval.exists() else None
    if approvals is not None and (approvals.get("candidateSetSha256") != full_status["candidateSetSha256"] or
                                  approvals.get("workbookSha256") != full_status["workbookSha256"]):
        raise ValueError("承認記録の候補またはレビュー表のハッシュが一致しません。")
    status = evaluate(rows, workbook_rows, {item["caseId"] for item in reviewed}, approvals, firstpass_rows)
    preliminary = ROOT / "comparison-shadow-preliminary-3-compact.json"
    if preliminary.exists():
        comparison = json.loads(preliminary.read_text(encoding="utf-8"))
        if (comparison.get("shadowPilot") is True and comparison.get("dryRun") is False and
                [case.get("CaseId") for case in comparison.get("cases", [])] ==
                ["qac-01", "checkmarx-09", "klocwork-01"]):
            status["shadowComparisonExecuted"] = True
    status["candidateSetSha256"] = full_status["candidateSetSha256"]
    status["workbookSha256"] = full_status["workbookSha256"]
    args.status.write_text(json.dumps(status, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if args.export:
        if not status["formalGoldReady"]:
            print(f"Gold Pilot未承認: {status['approvedCount']}/8件。出力していません。")
            return 2
        destination = args.export.resolve()
        if ROOT.resolve() not in destination.parents:
            raise ValueError("出力先は reports/generated 内に限定します。")
        if destination.exists() or destination.suffix.lower() != ".json":
            raise ValueError("出力先は未作成のJSONファイルにしてください。")
        by_id = {item["caseId"]: item for item in reviewed}
        export_rows = []
        for item in rows:
            case_id = item["caseId"]
            row = dict(by_id[case_id])
            row["goldDecision"] = next(a["decision"] for a in approvals["cases"] if a["caseId"] == case_id)
            export_rows.append(row)
        destination.write_text(json.dumps({"schemaVersion": 1, "candidateSetSha256": full_status["candidateSetSha256"],
                                           "holdoutUsed": False, "shadowComparisonExecuted": status["shadowComparisonExecuted"],
                                           "formalQualityComparisonExecuted": False,
                                           "cases": export_rows}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Gold Pilot: shadowPilotReady={'YES' if status['shadowPilotReady'] else 'NO'}, "
          f"formalGoldReady={'YES' if status['formalGoldReady'] else 'NO'}。状態表: {args.status}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
