"""Validate a human-reviewed closed-case workbook before exporting a golden set.

Only case IDs and missing-field names are written to the status report. The
candidate text and reviewer input remain in the private generated directory.
"""

from __future__ import annotations

import argparse
import copy
import datetime as dt
import hashlib
import json
import posixpath
import re
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


NS = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main",
      "r": "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
      "p": "http://schemas.openxmlformats.org/package/2006/relationships"}
CELL_COLUMN = re.compile(r"^[A-Z]+")
EXCEL_LITERAL_PREFIX = re.compile(r"^[=+\-@]")
HEADERS = [
    "案件ID", "製品", "分類", "マスク済み質問", "過去返信案", "匿名化確認",
    "回答内容確認", "期待する主張", "禁止する主張", "必要な根拠", "許容Readiness",
    "送信済み・承認済み回答の証跡", "確認者", "確認日", "追加点検",
]
SPLITS = [("開発用", "development", 40), ("未使用評価用", "holdout", 20)]
READINESS = {"CustomerReady", "NeedsReview", "NeedsCustomerConfirmation",
             "NeedsManufacturerConfirmation", "InsufficientEvidence", "Blocked"}
PLACEHOLDERS = {"未確認", "未記入", "確認中", "TBD", "N/A", "なし", "-", "—"}
FIELDS = {
    "F": "匿名化確認", "G": "回答内容確認", "H": "期待する主張", "I": "禁止する主張",
    "J": "必要な根拠", "K": "許容Readiness", "L": "送信済み・承認済み回答の証跡",
    "M": "確認者", "N": "確認日",
}


def _cell_text(cell: ET.Element, strings: list[str]) -> str:
    if cell.find("m:f", NS) is not None:
        raise ValueError("数式セルがあります。入力欄には値だけを記入してください。")
    kind = cell.get("t")
    if kind == "inlineStr":
        return "".join(t.text or "" for t in cell.findall(".//m:t", NS))
    value = cell.find("m:v", NS)
    if value is None or value.text is None:
        return ""
    return strings[int(value.text)] if kind == "s" else value.text


def read_review_workbook(path: Path) -> dict[str, list[dict[str, str]]]:
    """Read only the known worksheet cells with Python's standard library."""
    with zipfile.ZipFile(path) as book:
        strings = []
        if "xl/sharedStrings.xml" in book.namelist():
            root = ET.fromstring(book.read("xl/sharedStrings.xml"))
            strings = ["".join(t.text or "" for t in si.findall(".//m:t", NS))
                       for si in root.findall("m:si", NS)]
        workbook = ET.fromstring(book.read("xl/workbook.xml"))
        relationships = ET.fromstring(book.read("xl/_rels/workbook.xml.rels"))
        targets = {item.get("Id"): item.get("Target") for item in relationships.findall("p:Relationship", NS)}
        sheets = {}
        for sheet in workbook.findall("m:sheets/m:sheet", NS):
            target = targets[sheet.get(f"{{{NS['r']}}}id")]
            filename = target.lstrip("/") if target.startswith("/") else posixpath.normpath(posixpath.join("xl", target))
            xml = ET.fromstring(book.read(filename))
            rows = []
            for row in xml.findall("m:sheetData/m:row", NS):
                values = {CELL_COLUMN.match(cell.get("r", "")).group(): _cell_text(cell, strings)
                          for cell in row.findall("m:c", NS)}
                rows.append((int(row.get("r")), values))
            sheets[sheet.get("name")] = rows
    return sheets


def _lines(value: str, allow_none: bool = False) -> list[str]:
    entries = [line.strip(" ・\t") for line in value.splitlines() if line.strip(" ・\t")]
    if allow_none and entries == ["なし"]:
        return []
    if not entries or any(entry.upper() in PLACEHOLDERS for entry in entries):
        return []
    return entries


def _date(value: str) -> str | None:
    try:
        if value.isdecimal() and int(value) > 30000:
            return (dt.date(1899, 12, 30) + dt.timedelta(days=int(value))).isoformat()
        return dt.date.fromisoformat(value).isoformat()
    except (ValueError, OverflowError):
        return None


def validate_row(values: dict[str, str]) -> tuple[list[str], dict]:
    missing = []
    for column in ("F", "G"):
        if values.get(column, "").strip() != "確認済":
            missing.append(FIELDS[column])
    expected = _lines(values.get("H", ""))
    forbidden_input = values.get("I", "").strip()
    forbidden = _lines(forbidden_input, allow_none=True)
    evidence = _lines(values.get("J", ""))
    if not expected:
        missing.append(FIELDS["H"])
    if not forbidden and forbidden_input != "なし":
        missing.append(FIELDS["I"])
    if not evidence:
        missing.append(FIELDS["J"])
    readiness = values.get("K", "").strip()
    if readiness not in READINESS:
        missing.append(FIELDS["K"])
    sent_proof = values.get("L", "").strip()
    if not sent_proof or sent_proof.upper() in PLACEHOLDERS:
        missing.append(FIELDS["L"])
    reviewer = values.get("M", "").strip()
    if not reviewer or reviewer.upper() in PLACEHOLDERS or reviewer == "Codex一次監査":
        missing.append(FIELDS["M"])
    review_date = _date(values.get("N", "").strip())
    if not review_date:
        missing.append(FIELDS["N"])
    approved = {
        "expectedClaims": expected, "forbiddenClaims": forbidden,
        "requiredEvidenceLocators": evidence, "expectedReadiness": readiness,
        "approvedReplyLocator": sent_proof, "reviewer": reviewer, "reviewDate": review_date,
        "humanPrivacyReviewed": True, "humanAnswerReviewed": True,
    }
    return missing, {} if missing else approved


def validate(candidate_path: Path, audit_path: Path, workbook_path: Path) -> tuple[dict, list[dict]]:
    candidate_bytes = candidate_path.read_bytes()
    digest = hashlib.sha256(candidate_bytes).hexdigest()
    candidate = json.loads(candidate_bytes)
    audit = json.loads(audit_path.read_text(encoding="utf-8"))
    if digest != audit["candidateSetSha256"]:
        raise ValueError("候補と一次監査のハッシュが一致しません。")
    cases = {case["caseId"]: case for case in candidate["cases"]}
    if len(cases) != 60:
        raise ValueError("候補IDが60件で一意ではありません。")
    sheets = read_review_workbook(workbook_path)
    if set(sheets) != {name for name, _, _ in SPLITS}:
        raise ValueError("レビュー表のシート構成が違います。")
    status = []
    reviewed = []
    seen = set()
    for sheet_name, split, expected_count in SPLITS:
        rows = dict(sheets[sheet_name])
        if [rows.get(4, {}).get(chr(65 + i), "") for i in range(15)] != HEADERS:
            raise ValueError(f"{sheet_name} の見出しが違います。")
        data_rows = [(number, values) for number, values in sheets[sheet_name] if number >= 5 and values.get("A")]
        if len(data_rows) != expected_count:
            raise ValueError(f"{sheet_name} の案件数が違います。")
        for number, values in data_rows:
            case_id = values["A"]
            case = cases.get(case_id)
            if not case or case_id in seen or case["split"] != split:
                raise ValueError(f"案件IDまたは区分が不正です: {sheet_name} 行{number}")
            seen.add(case_id)
            original = [case["caseId"], case["product"], case["topic"],
                        case["question"], case["historicalReplyDraft"]]
            original = [("'" + value) if EXCEL_LITERAL_PREFIX.match(value) else value for value in original]
            if [values.get(chr(65 + i), "") for i in range(5)] != original:
                raise ValueError(f"元の候補欄が変更されています: {case_id}")
            missing, approved = validate_row(values)
            status.append({"caseId": case_id, "split": split, "reviewComplete": not missing,
                           "missingFields": missing})
            if not missing:
                row = copy.deepcopy(case)
                row.update(approved)
                reviewed.append(row)
    if seen != set(cases):
        raise ValueError("レビュー表に候補の欠落があります。")
    return {"schemaVersion": 1, "candidateSetSha256": digest,
            "workbookSha256": hashlib.sha256(workbook_path.read_bytes()).hexdigest(),
            "approvedCount": len(reviewed), "totalCount": len(cases), "cases": status}, reviewed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    root = Path(__file__).resolve().parent / "reports" / "generated"
    parser.add_argument("--candidate", type=Path, default=root / "closed-case-candidates.json")
    parser.add_argument("--audit", type=Path, default=root / "closed-case-review-audit.json")
    parser.add_argument("--workbook", type=Path, default=root / "outputs" / "01a0dae8-6eac-7081-971a-f8d6aa9a042c" / "closed-case-review.xlsx")
    parser.add_argument("--status", type=Path, default=root / "closed-case-review-status.json")
    parser.add_argument("--export", type=Path, help="Export reviewed private JSON only when all 60 rows are approved")
    args = parser.parse_args()
    status, reviewed = validate(args.candidate, args.audit, args.workbook)
    args.status.parent.mkdir(parents=True, exist_ok=True)
    args.status.write_text(json.dumps(status, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if args.export:
        if len(reviewed) != 60:
            print(f"承認済み {len(reviewed)}/60件。評価用データは出力していません。")
            return 2
        generated = root.resolve()
        destination = args.export.resolve()
        if generated not in destination.parents:
            raise ValueError("評価用データの出力先は reports/generated 内に限定します。")
        destination.write_text(json.dumps({"schemaVersion": 1, "candidateSetSha256": status["candidateSetSha256"],
                                           "humanReviewRequired": False, "cases": reviewed}, ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8")
    print(f"承認済み {len(reviewed)}/60件。状態表: {args.status}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
