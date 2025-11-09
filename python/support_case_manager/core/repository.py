"""
Repository and storage helpers for Support Case Manager.
"""

from __future__ import annotations

from datetime import datetime
import json
import logging
import os
from pathlib import Path
from typing import Iterable, List

from support_case_manager.core.cases import (
    CaseRecord,
    build_folder_name,
    ensure_unique_cases,
    format_support_number,
    normalize_support_number,
    parse_case_from_directory,
    sanitize_component,
    split_status_with_legacy,
)
from support_case_manager.core.notes import NoteDefinition, NOTE_DEFINITIONS, ensure_note_file


class CaseRepository:
    def __init__(self, logger: logging.Logger) -> None:
        self._logger = logger
        self._base_path: Path | None = None
        self._index_path: Path | None = None
        self._case_index: list[CaseRecord] = []

    @property
    def base_path(self) -> Path | None:
        return self._base_path

    def set_base_path(self, base: str | Path | None) -> None:
        if not base:
            self._base_path = None
            self._index_path = None
            self._case_index = []
            return
        resolved = Path(base).expanduser()
        resolved.mkdir(parents=True, exist_ok=True)
        self._base_path = resolved
        self._index_path = resolved / "cases-index.json"
        self._case_index = self._load_index()

    # ------------------------------------------------------------------ index helpers
    def _load_index(self) -> list[CaseRecord]:
        if not self._index_path or not self._index_path.exists():
            return []
        try:
            data = json.loads(self._index_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            self._logger.warning("Failed to read cases-index.json: %s", exc)
            return []
        records: list[CaseRecord] = []
        payload = data if isinstance(data, list) else [data]
        for item in payload:
            try:
                records.append(CaseRecord.from_dict(item))
            except Exception as exc:  # noqa: BLE001
                self._logger.debug("Skip corrupted index entry: %s", exc)
        return records

    def _save_index(self) -> None:
        if not self._index_path:
            return
        try:
            serializable = [case.to_dict() for case in self._case_index]
            self._index_path.write_text(json.dumps(serializable, ensure_ascii=False, indent=2), encoding="utf-8")
        except OSError as exc:
            self._logger.error("Failed to write cases-index.json: %s", exc)

    # ------------------------------------------------------------------ queries
    def list_categories(self) -> list[str]:
        base = self._base_path
        if not base or not base.exists():
            return []
        return sorted([entry.name for entry in base.iterdir() if entry.is_dir()])

    def all_cases(self) -> list[CaseRecord]:
        filesystem = self._scan_folders()
        merged = self._merge_cases(self._case_index, filesystem)
        self._case_index = [
            CaseRecord.from_dict({**case.to_dict(), "is_from_folder": False}) for case in merged
        ]
        self._save_index()
        return merged

    def find_by_support(self, support_number: str) -> CaseRecord | None:
        normalized = normalize_support_number(support_number)
        if not normalized:
            return None
        for case in self._case_index:
            if case.normalized_support == normalized:
                return case
        for case in self._scan_folders():
            if case.normalized_support == normalized:
                return case
        return None

    # ------------------------------------------------------------------ mutations
    def create_case(
        self,
        *,
        company: str,
        support_number: str,
        status: str,
        created_on: datetime | str,
        category: str = "",
        open_after: bool = False,
    ) -> CaseRecord:
        if not self._base_path:
            raise ValueError("ベースフォルダが設定されていません。")

        normalized_support = normalize_support_number(support_number)
        if normalized_support and self.find_by_support(normalized_support):
            raise ValueError(f"サポート番号 {support_number} の案件は既に存在します。")

        target_root = self._base_path
        cleaned_category = sanitize_component(category)
        if cleaned_category:
            target_root = target_root / cleaned_category
            target_root.mkdir(parents=True, exist_ok=True)

        folder_name = self._next_folder_name(target_root, company, support_number, status, created_on)
        folder_path = target_root / folder_name
        folder_path.mkdir(parents=True, exist_ok=False)

        case = CaseRecord(
            company=company,
            support_number=support_number,
            status=status,
            created_on=created_on if isinstance(created_on, str) else created_on.strftime("%Y%m%d"),
            folder_name=folder_name,
            folder_path=folder_path,
            last_updated=datetime.utcnow().isoformat(),
            category=cleaned_category,
        )

        for note in NOTE_DEFINITIONS:
            ensure_note_file(folder_path, note, support_number)

        self._case_index.append(case)
        self._save_index()
        if open_after:
            self._open_folder(folder_path)
        return case

    def _next_folder_name(
        self,
        root: Path,
        company: str,
        support_number: str,
        status: str,
        created_on: datetime | str,
    ) -> str:
        suffix = datetime.now().strftime("%Y%m%d")
        base_name = build_folder_name(created_on, company, support_number, status, suffix)
        candidate = root / base_name
        counter = 2
        while candidate.exists():
            candidate = root / f"{base_name}_{counter}"
            counter += 1
        return candidate.name

    def update_case_entry(self, case: CaseRecord) -> None:
        replaced = False
        for idx, existing in enumerate(self._case_index):
            if existing.folder_path == case.folder_path or (
                case.normalized_support and case.normalized_support == existing.normalized_support
            ):
                self._case_index[idx] = case
                replaced = True
                break
        if not replaced:
            self._case_index.append(case)
        self._save_index()

    @staticmethod
    def _open_folder(path: Path) -> None:
        try:
            if os.name == "nt":
                os.startfile(str(path))  # type: ignore[attr-defined]
            else:  # pragma: no cover
                import subprocess

                subprocess.Popen(["xdg-open", str(path)])
        except OSError:
            pass

    # ------------------------------------------------------------------ filesystem helpers
    def _scan_folders(self) -> list[CaseRecord]:
        base = self._base_path
        if not base or not base.exists():
            return []
        stack = [base]
        found: list[CaseRecord] = []
        while stack:
            current = stack.pop()
            try:
                with os.scandir(current) as entries:
                    for entry in entries:
                        if entry.is_dir(follow_symlinks=False):
                            path = Path(entry.path)
                            case = parse_case_from_directory(path)
                            if case:
                                found.append(case)
                            stack.append(path)
            except OSError as exc:
                self._logger.debug("Directory scan skipped for %s: %s", current, exc)
        return found

    @staticmethod
    def _merge_cases(indexed: Iterable[CaseRecord], folders: Iterable[CaseRecord]) -> list[CaseRecord]:
        ordered_index = sorted(indexed, key=lambda c: c.last_updated, reverse=True)
        ordered_folder = sorted(folders, key=lambda c: c.last_updated, reverse=True)
        combined = ensure_unique_cases([*ordered_index, *ordered_folder])
        combined.sort(key=lambda c: c.last_updated, reverse=True)
        return combined
