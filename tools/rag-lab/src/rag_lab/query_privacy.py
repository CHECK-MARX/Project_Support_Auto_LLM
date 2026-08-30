from __future__ import annotations

import re
import unicodedata


_EMAIL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
_PHONE = re.compile(r"(?<!\d)(?:0\d{1,4}[- ]?)?\d{2,4}[- ]?\d{3,4}(?!\d)|内線\s*\d{3,}")
_POSTAL = re.compile(r"(?<![\d-])〒?\d{3}-?\d{4}(?!\d)")
_DATE = re.compile(
    r"(?:\d{4}[/-]\d{1,2}[/-]\d{1,2}|\d{4}年\d{1,2}月\d{1,2}日)(?:\s+\d{1,2}:\d{2}(?::\d{2})?)?"
)
_RECIPIENT_LABEL = re.compile(r"(?:担当者(?:名)?|ご担当者|氏名|お名前)\s*[:：]")
_COMPANY = re.compile(r"(?:株式会社|有限会社|合同会社)[^\s、,，:：。]{1,48}?(?=(?:の|様|御中|担当者|電話|$|\s))")


def sensitive_query_categories(query: str) -> tuple[str, ...]:
    """Return redacted PII categories detected in a retrieval query.

    This intentionally returns categories rather than source values so evaluation
    reports remain safe to retain. It measures query contamination, not words in
    retrieved evidence.
    """

    normalized = unicodedata.normalize("NFKC", query)
    categories: list[str] = []
    for category, pattern in (
        ("email", _EMAIL),
        ("phone_or_extension", _PHONE),
        ("postal_code", _POSTAL),
        ("date_or_time", _DATE),
        ("recipient_label", _RECIPIENT_LABEL),
        ("company_name", _COMPANY),
    ):
        if pattern.search(normalized):
            categories.append(category)
    return tuple(categories)
