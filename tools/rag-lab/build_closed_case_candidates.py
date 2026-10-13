"""Build a local, review-required candidate set from closed case note pairs.

Source paths and original note text are never written to the output. The output
is still private until a person verifies anonymity and answer correctness.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import Counter
from pathlib import Path


QUESTION_NAMES = ("お客様ご相談内容", "お問い合わせ内容")
ANSWER_NAMES = ("お客様への返信案", "回答案")
PERSONAL_LINE = re.compile(
    r"株式会社|有限会社|合同会社|[（(]株[）)]|㈱|御中|"
    r"(?<!同)(?<!仕)(?<!客)[\u3040-\u30ff\u3400-\u9fffA-Za-zー・]+"
    r"(?:[\s　]+[\u3040-\u30ff\u3400-\u9fffA-Za-zー・]+)?[\s　]*(?:様|さま)"
    r"(?=$|[\s　、。,，．:：])|"
    r"(?<!客)(?<!同)(?<!仕)様(?!々|式|子)|"
    r"[\u4e00-\u9fff]{1,8}さん(?:\s|$|[、,])|担当者|東陽テクニカ|"
    r"パナソニック|"
    r"お世話になっております|よろしくお願いいたします|"
    r"(?:^|\s)(?:TEL|FAX|Phone|Mobile)\s*[:：]|"
    r"(?:事業|開発|営業)(?:部|センター)|"
    r"(?:^|\s)(?:To|From|Cc|Bcc|Subject)\s*[:：]|"
    r"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|"
    r"(?<!\d)0\d{1,4}[- ]\d{1,4}[- ]\d{3,4}(?!\d)",
    re.IGNORECASE,
)
REDACTIONS = (
    (re.compile(r"TOYO(?:[_ -][A-Za-z0-9]+){0,4}", re.IGNORECASE), "[ORGANIZATION_RESOURCE]"),
    (re.compile(r"https?://\S+", re.IGNORECASE), "[URL]"),
    (re.compile(r"(?<![\w.-])(?:[A-Za-z0-9-]+\.)+(?:com|net|org|co\.jp|jp|io|local|internal)(?![\w.-])"), "[HOSTNAME]"),
    (re.compile(r"\\\\[^\s\\]+\\[^\s]+"), "[NETWORK_PATH]"),
    (re.compile(r"[A-Za-z]:\\[^\s]+"), "[LOCAL_PATH]"),
    (re.compile(r"(?<![\w/\\])[^\s/\\<>:\"']+\.(?:pdf|xlsx?|docx?|pptx?|zip|log|csv|txt|json)\b", re.IGNORECASE), "[ATTACHMENT]"),
    (re.compile(r"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)"), "[IP_ADDRESS]"),
    (re.compile(r"(?<!\d)0{3,}\d{3,}(?!\d)"), "[SUPPORT_ID]"),
    (re.compile(r"〒?\d{3}-\d{4}"), "[POSTAL_CODE]"),
)
RESIDUAL_PII = re.compile(
    r"TOYO(?:[_ -][A-Za-z0-9]+){0,4}|"
    r"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|https?://|"
    r"(?<![\w.-])(?:[A-Za-z0-9-]+\.)+(?:com|net|org|co\.jp|jp|io|local|internal)(?![\w.-])|"
    r"[A-Za-z]:\\|\\\\[^\s\\]+\\|"
    r"(?<![\w/\\])[^\s/\\<>:\"']+\.(?:pdf|xlsx?|docx?|pptx?|zip|log|csv|txt|json)\b|"
    r"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)|"
    r"(?<!\d)0{3,}\d{3,}(?!\d)|株式会社|有限会社|合同会社|御中|"
    r"〒?\d{3}-\d{4}",
    re.IGNORECASE,
)
TOPIC_RULES = (
    ("cli", re.compile(r"CLI|コマンド|command|qacli|cxcli|実行", re.IGNORECASE)),
    ("version", re.compile(r"バージョン|version|アップデート|upgrade|更新", re.IGNORECASE)),
    ("error", re.compile(r"エラー|error|例外|失敗|不具合", re.IGNORECASE)),
    ("configuration", re.compile(r"設定|構成|config|インストール|導入", re.IGNORECASE)),
)


def _read_note(path: Path) -> str | None:
    if path.stat().st_size > 100_000:
        return None
    raw = path.read_bytes()
    for encoding in ("utf-8-sig", "cp932"):
        try:
            return raw.decode(encoding)
        except UnicodeDecodeError:
            pass
    return None


def _sanitize(text: str) -> str:
    kept: list[str] = []
    kept_chars = 0
    for raw_line in text.replace("\r\n", "\n").replace("\r", "\n").split("\n"):
        line = raw_line.strip()
        if not line or len(line) > 700 or line.startswith(">"):
            continue
        if "追記部" in line and ("***" in line or "---" in line):
            continue
        if PERSONAL_LINE.search(line):
            if kept_chars >= 35:
                break
            continue
        for pattern, replacement in REDACTIONS:
            line = pattern.sub(replacement, line)
        if line and line not in kept[-1:]:
            kept.append(line)
            kept_chars += len(line)
    return "\n".join(kept).strip()


def _latest_named(notes: list[Path], names: tuple[str, ...]) -> Path | None:
    matches = [note for note in notes if note.stem.startswith(names)]
    return max(matches, key=lambda note: note.stat().st_mtime_ns) if matches else None


def _candidate(case_folder: Path, product: str) -> dict | None:
    notes = [path for path in case_folder.glob("*.txt") if path.is_file() and not path.is_symlink()]
    question_file = _latest_named(notes, QUESTION_NAMES)
    answer_file = _latest_named(notes, ANSWER_NAMES)
    if question_file is None or answer_file is None:
        return None
    question_raw = _read_note(question_file)
    answer_raw = _read_note(answer_file)
    if question_raw is None or answer_raw is None:
        return None
    question = _sanitize(question_raw)
    answer = _sanitize(answer_raw)
    if not 35 <= len(question) <= 2500 or not 80 <= len(answer) <= 4000:
        return None
    if RESIDUAL_PII.search(question + "\n" + answer):
        return None
    digest = hashlib.sha256(str(case_folder).casefold().encode("utf-8")).hexdigest()[:20]
    topic = next((name for name, rule in TOPIC_RULES if rule.search(question)), "other")
    return {
        "candidateId": digest,
        "product": product,
        "topic": topic,
        "question": question,
        "historicalReplyDraft": answer,
        "questionSha256": hashlib.sha256(question.encode("utf-8")).hexdigest(),
        "answerSha256": hashlib.sha256(answer.encode("utf-8")).hexdigest(),
        "expectedClaims": [],
        "forbiddenClaims": [],
        "requiredEvidenceLocators": [],
        "expectedReadiness": None,
        "humanPrivacyReviewed": False,
        "humanAnswerReviewed": False,
    }


def _remove_shared_boilerplate(candidates: list[dict]) -> list[dict]:
    line_frequency: Counter[str] = Counter()
    for item in candidates:
        line_frequency.update(set(
            item["question"].splitlines() + item["historicalReplyDraft"].splitlines()
        ))
    threshold = max(10, len(candidates) // 5)
    boilerplate = {line for line, frequency in line_frequency.items() if frequency >= threshold}
    cleaned: list[dict] = []
    for item in candidates:
        question = "\n".join(line for line in item["question"].splitlines() if line not in boilerplate)
        answer = "\n".join(
            line for line in item["historicalReplyDraft"].splitlines() if line not in boilerplate
        )
        if not 35 <= len(question) <= 2500 or not 80 <= len(answer) <= 4000:
            continue
        cleaned.append(item | {
            "question": question,
            "historicalReplyDraft": answer,
            "questionSha256": hashlib.sha256(question.encode("utf-8")).hexdigest(),
            "answerSha256": hashlib.sha256(answer.encode("utf-8")).hexdigest(),
        })
    return cleaned


def build(
    roots: dict[str, Path], output: Path, per_product: int = 30,
    selection_counts: dict[str, int] | None = None,
    development_counts: dict[str, int] | None = None,
) -> dict:
    if per_product <= 0:
        raise ValueError("per_product must be positive")
    selection_counts = selection_counts or {product: per_product for product in roots}
    development_counts = development_counts or {
        product: selection_counts[product] * 2 // 3 for product in roots
    }
    if set(selection_counts) != set(roots) or set(development_counts) != set(roots):
        raise ValueError("Selection and split counts must cover every product")
    if any(selection_counts[product] <= 0 or not 0 < development_counts[product] < selection_counts[product]
           for product in roots):
        raise ValueError("Each product needs development and holdout cases")
    all_candidates: dict[str, list[dict]] = {}
    scanned = Counter()
    for product, root in roots.items():
        if not root.is_dir():
            raise FileNotFoundError(f"Source root is not a directory: {product}")
        candidates: list[dict] = []
        for group in sorted(root.iterdir()):
            if not group.is_dir() or group.is_symlink():
                continue
            for case_folder in sorted(group.iterdir()):
                if not case_folder.is_dir() or case_folder.is_symlink():
                    continue
                scanned[product] += 1
                candidate = _candidate(case_folder, product)
                if candidate is not None:
                    candidates.append(candidate)
        candidates = _remove_shared_boilerplate(candidates)
        if len(candidates) < selection_counts[product]:
            raise ValueError(f"Too few masked candidates for {product}: {len(candidates)}")
        # Each topic contributes in turn; fixed hashes make reruns reproducible.
        buckets: dict[str, list[dict]] = {}
        for candidate in candidates:
            buckets.setdefault(candidate["topic"], []).append(candidate)
        for bucket in buckets.values():
            bucket.sort(key=lambda item: item["candidateId"])
        selected: list[dict] = []
        while len(selected) < selection_counts[product]:
            for topic in sorted(buckets):
                if buckets[topic] and len(selected) < selection_counts[product]:
                    selected.append(buckets[topic].pop(0))
        all_candidates[product] = selected

    development: list[dict] = []
    holdout: list[dict] = []
    for product, selected in all_candidates.items():
        for index, item in enumerate(selected):
            item["caseId"] = f"{product.lower()}-{index + 1:02d}"
            if index < development_counts[product]:
                item["split"] = "development"
                development.append(item)
            else:
                item["split"] = "holdout"
                holdout.append(item)

    result = {
        "schemaVersion": 1,
        "dataClassification": "private_masked_review_required",
        "referenceAnswerStatus": "historical_draft_not_verified_final",
        "sameCaseRetrievalExcluded": False,
        "developmentCount": len(development),
        "holdoutCount": len(holdout),
        "cases": development + holdout,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    return {"scanned": dict(scanned), "selected": {p: len(c) for p, c in all_candidates.items()},
            "development": len(development), "holdout": len(holdout)}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--qac-root", type=Path, required=True)
    parser.add_argument("--checkmarx-root", type=Path, required=True)
    parser.add_argument("--klocwork-root", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    roots = {"QAC": args.qac_root, "CHECKMARX": args.checkmarx_root}
    if args.klocwork_root is not None:
        roots["KLOCWORK"] = args.klocwork_root
        result = build(roots, args.output, selection_counts={
            "QAC": 25, "CHECKMARX": 24, "KLOCWORK": 11,
        }, development_counts={
            "QAC": 17, "CHECKMARX": 16, "KLOCWORK": 7,
        })
    else:
        result = build(roots, args.output)
    print(json.dumps(result))


if __name__ == "__main__":
    main()
