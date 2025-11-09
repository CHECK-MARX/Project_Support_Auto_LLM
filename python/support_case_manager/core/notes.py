"""
Note file utilities.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
import logging
from pathlib import Path
import shutil
from typing import Iterable

from support_case_manager.core.cases import format_support_number, sanitize_component

CRLF = "\r\n"
LOGGER = logging.getLogger("support_case_manager.notes")


@dataclass(frozen=True)
class NoteDefinition:
    key: str
    label: str
    base_name: str
    folder_prefix: str

    def file_name(self, support_number: str) -> str:
        formatted = sanitize_component(format_support_number(support_number))
        suffix = f"_{formatted}" if formatted else ""
        return f"{self.base_name}{suffix}.txt"

    def folder_name(self, support_number: str, counter: int = 0) -> str:
        today = datetime.now().strftime("%Y%m%d")
        formatted = sanitize_component(format_support_number(support_number))
        suffix = f"_{formatted}" if formatted else ""
        base = f"{self.folder_prefix}_{today}{suffix}"
        if counter > 0:
            return f"{base}_{counter}"
        return base


NOTE_DEFINITIONS: list[NoteDefinition] = [
    NoteDefinition("consult", "お客様ご相談内容", "お客様ご相談内容", "お客様ご相談内容"),
    NoteDefinition("reply", "お客様への返信案", "お客様への返信案", "返信案"),
    NoteDefinition("vendor", "メーカー連携内容", "メーカー連携内容", "メーカー連携内容"),
]


def get_note_definition(key: str) -> NoteDefinition:
    for definition in NOTE_DEFINITIONS:
        if definition.key == key:
            return definition
    return NOTE_DEFINITIONS[0]


def ensure_note_file(folder: Path, definition: NoteDefinition, support_number: str) -> Path:
    folder.mkdir(parents=True, exist_ok=True)
    target = folder / definition.file_name(support_number)
    if not target.exists():
        target.write_text("", encoding="utf-8", newline=CRLF)
    return target


def _read_text_lossless(path: Path) -> str:
    if not path.exists():
        return ""
    data = path.read_bytes()
    for encoding in ("utf-8", "utf-8-sig", "cp932", "shift_jis"):
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            continue
    LOGGER.warning("Failed to decode %s with common encodings; using replacement characters.", path)
    return data.decode("utf-8", errors="replace")


def atomic_append(path: Path, text: str) -> None:
    original = _read_text_lossless(path)
    new_content = original.rstrip("\r\n") + (CRLF if original else "") + text
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(new_content, encoding="utf-8", newline=CRLF)
    temp.replace(path)


def append_note(
    folder: Path,
    definition: NoteDefinition,
    support_number: str,
    status: str,
    body: str,
) -> Path:
    path = ensure_note_file(folder, definition, support_number)
    timestamp = datetime.now().strftime("%Y/%m/%d %H:%M:%S")
    header = f"*****追記部_{timestamp}({status})******"
    footer = "--------------------------------------------------"
    payload = CRLF.join([header, body.strip(), footer])
    atomic_append(path, payload)
    return path


def copy_existing_text(source: Path, folder: Path, definition: NoteDefinition, support_number: str) -> Path:
    if not source.exists():
        raise FileNotFoundError(str(source))
    target = folder / definition.file_name(support_number)
    folder.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)
    return target


def create_subfolder(folder: Path, definition: NoteDefinition, support_number: str) -> Path:
    base = definition.folder_name(support_number)
    candidate = folder / base
    counter = 1
    while candidate.exists():
        candidate = folder / definition.folder_name(support_number, counter)
        counter += 1
    candidate.mkdir(parents=True, exist_ok=False)
    return candidate


def note_labels() -> Iterable[str]:
    return [definition.label for definition in NOTE_DEFINITIONS]
