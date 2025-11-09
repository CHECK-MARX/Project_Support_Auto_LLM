"""
Core data structures and helpers for Support Case Manager.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date, datetime
from pathlib import Path
import re
from typing import Any, Iterable

INVALID_CHARS = '<>:"/\\|?*'
_INVALID_TRANS = str.maketrans({char: "_" for char in INVALID_CHARS})
_FOLDER_REGEX = re.compile(r"^(?P<date>\d{8})\((?P<inner>.+)\)(?P<status>.+)$")
_LEGACY_REGEX = re.compile(r"^(?P<date>\d{4,8})\((?P<inner>.+?)\)(?P<status>.+)$")
_STATUS_STAMP_REGEX = re.compile(r"^(?P<body>.*?)[_\-\s](?P<stamp>\d{8})$")
SUPPORT_PAD_LENGTH = 8
_LEGACY_STAMP_REGEX = re.compile(r"^(?P<body>.*?)(?P<stamp>\d{4,8})$")


def sanitize_component(text: str) -> str:
    if not text:
        return ""
    return text.strip().translate(_INVALID_TRANS)


def normalize_status(text: str) -> str:
    return split_status_and_stamp(text)[0]


def split_status_and_stamp(text: str) -> tuple[str, str]:
    if not text:
        return "", ""
    status = text.strip()
    stamp = ""
    while True:
        match = _STATUS_STAMP_REGEX.match(status)
        if not match:
            break
        candidate = match.group("body").rstrip("_- ")
        stamp = match.group("stamp")
        status = candidate or status
        if stamp:
            break
    return status, stamp


def split_status_with_legacy(text: str) -> tuple[str, str]:
    status, stamp = split_status_and_stamp(text)
    if stamp:
        return status, stamp
    match = _LEGACY_STAMP_REGEX.match(status)
    if match:
        body = match.group("body").rstrip("_- ")
        digits = match.group("stamp")
        return body or status, digits
    return status, ""


def ensure_date_string(value: str | date | datetime) -> str:
    if isinstance(value, datetime):
        return value.strftime("%Y%m%d")
    if isinstance(value, date):
        return value.strftime("%Y%m%d")
    text = str(value).strip()
    if not text:
        raise ValueError("Date value is required.")
    if len(text) == 6 and text.isdigit():
        return f"{text}01"
    if len(text) == 4 and text.isdigit():
        return f"{text}0101"
    if len(text) == 8 and text.isdigit():
        return text
    try:
        parsed = datetime.fromisoformat(text)
        return parsed.strftime("%Y%m%d")
    except ValueError as exc:
        raise ValueError(f"Unsupported date string: {text}") from exc


def normalize_support_number(text: str) -> str:
    if not text:
        return ""
    compact = "".join(ch for ch in text.strip() if ch.isalnum())
    if not compact:
        return ""
    if compact.isdigit():
        return compact.zfill(SUPPORT_PAD_LENGTH)
    return compact.upper()


def format_support_number(text: str) -> str:
    """Format a user-entered support number for display/folder naming."""
    return normalize_support_number(text)


def build_folder_name(
    created_on: str | date | datetime,
    company: str,
    support_number: str,
    status: str,
    updated_stamp: str | None = None,
) -> str:
    date_text = ensure_date_string(created_on)
    safe_company = sanitize_component(company)
    if not safe_company:
        raise ValueError("Company name is required.")

    safe_support = sanitize_component(format_support_number(support_number))
    safe_status = sanitize_component(normalize_status(status))
    if not safe_status:
        raise ValueError("Status is required.")

    inner = safe_company if not safe_support else f"{safe_company}_{safe_support}"
    stamp = sanitize_component(updated_stamp or datetime.now().strftime("%Y%m%d"))
    folder = f"{date_text}({inner}){safe_status}"
    if stamp:
        folder = f"{folder}_{stamp}"
    return folder


def default_note_name(base: str, support_number: str) -> str:
    prefix = sanitize_component(base) or "note"
    support = sanitize_component(format_support_number(support_number))
    if support:
        return f"{prefix}_{support}.txt"
    return f"{prefix}.txt"


def to_iso_timestamp(value: datetime | str | None = None) -> str:
    if isinstance(value, datetime):
        return value.isoformat()
    if isinstance(value, str) and value:
        try:
            return datetime.fromisoformat(value).isoformat()
        except ValueError:
            pass
    return datetime.utcnow().isoformat()


@dataclass(slots=True)
class CaseRecord:
    company: str
    support_number: str
    status: str
    created_on: str
    folder_name: str
    folder_path: Path
    last_updated: str
    category: str = ""
    is_from_folder: bool = False
    normalized_support: str = field(init=False)

    def __post_init__(self) -> None:
        self.company = self.company.strip()
        self.support_number = format_support_number(self.support_number)
        self.status = normalize_status(self.status or "")
        self.created_on = ensure_date_string(self.created_on)
        self.folder_path = Path(self.folder_path)
        self.folder_name = self.folder_name or self.folder_path.name
        self.last_updated = to_iso_timestamp(self.last_updated)
        self.category = self.category.strip()
        self.normalized_support = normalize_support_number(self.support_number)

    def to_dict(self) -> dict[str, Any]:
        return {
            "company": self.company,
            "support_number": self.support_number,
            "status": self.status,
            "created_on": self.created_on,
            "folder_name": self.folder_name,
            "folder_path": str(self.folder_path),
            "last_updated": self.last_updated,
            "category": self.category,
            "is_from_folder": self.is_from_folder,
        }

    @classmethod
    def from_dict(cls, payload: dict[str, Any]) -> "CaseRecord":
        return cls(
            company=payload.get("company", ""),
            support_number=payload.get("support_number", ""),
            status=payload.get("status", ""),
            created_on=payload.get("created_on", datetime.utcnow().strftime("%Y%m%d")),
            folder_name=payload.get("folder_name", ""),
            folder_path=Path(payload.get("folder_path", "")),
            last_updated=payload.get("last_updated", datetime.utcnow().isoformat()),
            category=payload.get("category", ""),
            is_from_folder=bool(payload.get("is_from_folder", False)),
        )

    def display_text(self) -> str:
        status = self.status or "未設定"
        return f"{self.created_on} ({self.company} {self.support_number or '-'}) {status}"


def parse_case_from_directory(directory: Path) -> CaseRecord | None:
    match = _FOLDER_REGEX.match(directory.name)
    legacy = False
    if not match:
        match = _LEGACY_REGEX.match(directory.name)
        if not match:
            return None
        legacy = True

    created = match.group("date")
    inner = match.group("inner").strip()
    status_raw = match.group("status").strip()

    company = inner
    support = ""
    if "_" in inner:
        company, _, tail = inner.rpartition("_")
        company = company.strip()
        support = tail.strip()
    else:
        legacy_inner = re.match(r"^(?P<body>.*?)(?P<digits>\d{3,})$", inner)
        if legacy_inner:
            company = legacy_inner.group("body").strip()
            support = legacy_inner.group("digits").strip()

    if legacy:
        created = ensure_date_string(created)

    status, stamp = split_status_with_legacy(status_raw)
    last_updated = directory.stat().st_mtime
    updated_iso = datetime.fromtimestamp(last_updated).isoformat()
    if stamp:
        try:
            updated_iso = datetime.strptime(stamp, "%Y%m%d").isoformat()
        except ValueError:
            pass

    return CaseRecord(
        company=company,
        support_number=support,
        status=status or status_raw,
        created_on=created,
        folder_name=directory.name,
        folder_path=directory,
        last_updated=updated_iso,
        is_from_folder=True,
    )


def ensure_unique_cases(cases: Iterable[CaseRecord]) -> list[CaseRecord]:
    seen_paths: set[str] = set()
    seen_supports: set[str] = set()
    unique: list[CaseRecord] = []
    for case in cases:
        path_key = str(case.folder_path).lower()
        if path_key in seen_paths:
            continue
        support_key = case.normalized_support
        if support_key and support_key in seen_supports:
            continue
        seen_paths.add(path_key)
        if support_key:
            seen_supports.add(support_key)
        unique.append(case)
    return unique
