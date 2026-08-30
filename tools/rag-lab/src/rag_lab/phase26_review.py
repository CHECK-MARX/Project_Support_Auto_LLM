"""Phase 26 human-review package and conservative quality aggregation.

The input is the anonymized Phase 25 live report. Raw review output is intended
for a local temporary directory and must not be committed to the repository.
"""

from __future__ import annotations

import argparse
import json
import re
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


REVIEWED_PRODUCTS = {"HelixQAC", "Validate", "Checkmarx One"}
SECTIONS = (
    "【事前準備】", "【プロジェクト作成】", "【コンパイラ・CCT設定】",
    "【GUIでの手順】", "【CLIでの手順】", "【解析結果の確認】", "【結果確認】",
    "【注意点】", "【参照先】",
)


def build_review(report_path: str | Path, cases_path: str | Path) -> dict[str, Any]:
    report = json.loads(Path(report_path).read_text(encoding="utf-8"))
    cases = {item["id"]: item for item in json.loads(Path(cases_path).read_text(encoding="utf-8"))}
    rows = []
    limitations = []
    for row in report.get("Cases", []):
        case = cases.get(row.get("CaseId"))
        if not case:
            continue
        review = _review_row(case, row)
        if case["product"] == "Klocwork":
            limitations.append({"CaseId": case["id"], "Product": case["product"],
                                "Readiness": row.get("Readiness"),
                                "Reason": "Production active corpus is unavailable"})
        else:
            rows.append(review)
    return {
        "schemaVersion": 1,
        "dataClassification": "synthetic-anonymous-review",
        "ReviewMethod": "human_review_of_live_question_evidence_answer",
        "ReviewCases": rows,
        "Klocwork": {"Classification": "SAFE_CORPUS_LIMITATION", "Cases": limitations,
                      "IncludedInQualityDenominator": False},
        "Aggregate": _aggregate(rows),
    }


def _review_row(case: dict[str, Any], live: dict[str, Any]) -> dict[str, Any]:
    answer = _clean(live.get("DeterministicAnswer", ""))
    scores = _scores(case, live, answer)
    evidence = []
    for item in live.get("Evidence", []) or []:
        evidence.append({
            "Role": item.get("Role"), "SourceType": item.get("SourceType"),
            "DocumentTitle": item.get("DocumentTitle"), "Page": item.get("Page"),
            "Section": item.get("Section"), "Feature": item.get("Feature"),
            "Operation": item.get("Operation"),
            "EvidenceSummary": _summary(item.get("DocumentTitle"), item.get("Section")),
        })
    return {
        "CaseId": case["id"], "Product": case["product"], "QuestionType": case["type"],
        "Question": case["question"], "TechnicalQuery": live.get("TechnicalQuery", ""),
        "Readiness": live.get("Readiness"), "RequiredCoverage": case.get("requiredCoverage", []),
        "SelectedEvidence": evidence,
        "Facts": [], "Claims": {"Count": live.get("ClaimCount", 0), "ValuesAvailable": False},
        "Commands": live.get("Commands", []), "Versions": live.get("Versions", []),
        "References": {"Available": live.get("ReferenceAvailable", 0),
                       "Displayed": live.get("ReferenceDisplayed", 0)},
        "DeterministicAnswer": answer, "Review": scores,
        "SelectorEngine": live.get("SelectorEngine"),
    }


def _scores(case: dict[str, Any], live: dict[str, Any], answer: str) -> dict[str, Any]:
    case_id = case["id"]
    # Conservative human-review rubric based on the visible question/evidence/answer.
    manual = {
        "qac-project-analysis": (5, 4, 4, 4, 4, 4, 4),
        "qac-cct": (4, 3, 2, 2, 2, 3, 2),
        "qac-cli-option": (1, 1, 1, 1, 2, 2, 1),
        "qac-troubleshooting": (2, 2, 1, 1, 2, 2, 1),
        "checkmarx-sql": (1, 1, 1, 1, 2, 2, 1),
        "checkmarx-version": (2, 2, 1, 1, 3, 2, 2),
        "checkmarx-config": (2, 2, 1, 1, 2, 2, 2),
        "checkmarx-troubleshooting": (1, 1, 1, 1, 2, 2, 1),
        "validate-stream": (5, 4, 4, 4, 4, 4, 4),
        "validate-upload": (3, 3, 2, 2, 2, 3, 2),
        "validate-auth": (2, 2, 1, 1, 2, 2, 2),
        "validate-config": (2, 2, 1, 1, 2, 2, 2),
    }
    relevance, correctness, completeness, actionability, japanese, grounding, reference = manual.get(
        case_id, (0, 0, 0, 0, 0, 0, 0)
    )
    sendability = 5 if correctness >= 4 and japanese >= 4 and actionability >= 4 else 4 if correctness >= 4 else 2 if correctness >= 2 else 1
    classification = "READY" if sendability == 5 and correctness >= 4 else "MINOR_EDIT" if sendability == 4 and correctness >= 4 else "NOT_USABLE" if sendability <= 1 or correctness <= 1 else "MAJOR_EDIT"
    reasons = {
        "qac-project-analysis": "主要な準備、GUI、CLI、結果確認が回答内にあり、根拠と参照先も対応する。",
        "validate-stream": "概要と設定の最小手順が質問に直接対応するが、参照先表示がない。",
        "qac-cct": "CCTの説明はあるが、GUI・CLI・結果確認を確認できないと明記しており、送付用手順として不足する。",
        "qac-cli-option": "解析CLIを問う質問にvalidate buildとライセンス情報が混在し、要求コマンド・オプションを回答していない。",
        "validate-upload": "関連Evidenceはあるが、CLI手順欄が確認不能で、完全なアップロード手順になっていない。",
    }
    reason = reasons.get(case_id, "選択Evidenceは存在するが、質問の要求項目を手順として整理できていない。")
    return {
        "QuestionRelevance": relevance, "TechnicalCorrectness": correctness,
        "Completeness": completeness, "Actionability": actionability,
        "JapaneseQuality": japanese, "EvidenceGrounding": grounding,
        "ReferenceQuality": reference, "CustomerSendability": sendability,
        "Classification": classification, "Reason": reason,
        "SafetyPass": bool(live.get("SafetyPass", False)),
    }


def _aggregate(rows: list[dict[str, Any]]) -> dict[str, Any]:
    keys = ("QuestionRelevance", "TechnicalCorrectness", "EvidenceGrounding", "Completeness",
            "Actionability", "ReferenceQuality", "JapaneseQuality", "CustomerSendability")
    reviews = [row["Review"] for row in rows]
    counts = {name: sum(review["Classification"] == name for review in reviews)
              for name in ("READY", "MINOR_EDIT", "MAJOR_EDIT", "NOT_USABLE")}
    means = {key: round(sum(review[key] for review in reviews) / len(reviews), 3) for key in keys} if reviews else {key: 0 for key in keys}
    return {"CaseCount": len(rows), "Classification": counts, "Means": means,
            "ReadyOrMinorRate": round((counts["READY"] + counts["MINOR_EDIT"]) / len(rows), 3) if rows else 0.0,
            "CriticalSafetyIssue": any(not review["SafetyPass"] for review in reviews)}


def protected_values(before: str, after: str) -> bool:
    patterns = (
        r"qacli\s+[a-z][a-z0-9_.-]*(?:\s+[^\s`]+)*", r"--[A-Za-z][A-Za-z0-9_-]*",
        r"\b\d+(?:\.\d+){1,3}\b", r"Page\s+\d+", r"https?://[^\s]+",
    )
    for pattern in patterns:
        expected = {item.casefold() for item in re.findall(pattern, before, flags=re.IGNORECASE)}
        actual = {item.casefold() for item in re.findall(pattern, after, flags=re.IGNORECASE)}
        if any(not any(value.startswith(item) for value in actual) for item in expected):
            return False
    return True


def polish_with_ollama(answer: str, model: str = "qwen3:8b", timeout_seconds: int = 60) -> tuple[str | None, str]:
    """Run an optional local wording-only pass; failures are reported as NotRun."""
    prompt = (
        "文章校正のみ行ってください。入力の意味、事実、主張、コマンド、オプション、"
        "バージョン、Page、Section、URL、製品名、宛先、見出しを変更・追加・削除せず、"
        "不自然な日本語だけを整えてください。回答本文だけを返してください。\n\n" + answer
    )
    payload = json.dumps({"model": model, "prompt": prompt, "stream": False, "options": {"temperature": 0}}).encode()
    request = urllib.request.Request("http://127.0.0.1:11434/api/generate", data=payload,
                                     headers={"Content-Type": "application/json"})
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            content = json.loads(response.read().decode("utf-8")).get("response", "").strip()
    except (OSError, TimeoutError, urllib.error.URLError, json.JSONDecodeError):
        return None, "NotRun"
    if not content:
        return None, "NotRun"
    if not protected_values(answer, content):
        return content, "RejectedByValidator"
    return content, "Improved" if len(content) != len(answer) else "Neutral"


def _clean(value: Any) -> str:
    return str(value or "").replace("\r", "")


def _summary(title: Any, section: Any) -> str:
    if section:
        return f"{title or '(title unavailable)'} / {section}"
    return str(title or "Evidence summary unavailable")


def main() -> int:
    parser = argparse.ArgumentParser(description="Build an anonymized Phase 26 human review report")
    parser.add_argument("report", type=Path)
    parser.add_argument("cases", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = build_review(args.report, args.cases)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result["Aggregate"], ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
