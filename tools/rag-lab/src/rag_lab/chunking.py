from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass, field
from enum import StrEnum

from .models import Chunk, Document
from .normalization import NormalizationOptions, normalize_text


class ChunkingStrategy(StrEnum):
    FIXED = "fixed"
    PARAGRAPH = "paragraph"
    HEADING = "heading"
    STRUCTURED = "structured"


@dataclass(frozen=True, slots=True)
class ChunkingOptions:
    strategy: ChunkingStrategy = ChunkingStrategy.FIXED
    max_characters: int = 1200
    overlap_characters: int = 120
    normalization: NormalizationOptions = field(default_factory=NormalizationOptions)

    def __post_init__(self) -> None:
        if isinstance(self.strategy, str):
            object.__setattr__(self, "strategy", ChunkingStrategy(self.strategy))
        if self.max_characters <= 0:
            raise ValueError("max_characters must be greater than zero")
        if self.overlap_characters < 0:
            raise ValueError("overlap_characters must be zero or greater")
        if self.overlap_characters >= self.max_characters:
            raise ValueError("overlap_characters must be less than max_characters")


_MARKDOWN_HEADING = re.compile(
    r"^\s{0,3}#{1,6}\s+(?P<title>.+?)\s*#*\s*$"
)
_PLAIN_HEADING = re.compile(
    r"^\s*(?P<title>(?:第[^\s]{1,20}[章節]|質問|お問い合わせ|問い合わせ|原因|対処|対応|解決策|回答|メーカー回答|お客様向け最終回答))\s*[:：]?\s*$"
)

_STRUCTURED_NAMES: tuple[tuple[str, tuple[str, ...]], ...] = (
    ("question", ("質問", "お問い合わせ", "問い合わせ")),
    ("cause", ("原因",)),
    ("resolution", ("対処", "対応", "解決策")),
    ("manufacturer_reply", ("メーカー回答",)),
    ("customer_final_reply", ("お客様向け最終回答", "最終回答", "回答")),
)
_STRUCTURED_LABELS = {
    "question": "質問",
    "cause": "原因",
    "resolution": "対処",
    "manufacturer_reply": "メーカー回答",
    "customer_final_reply": "お客様向け最終回答",
}


def _fixed_pieces(text: str, maximum: int, overlap: int) -> list[str]:
    if not text:
        return []
    pieces: list[str] = []
    step = maximum - overlap
    start = 0
    while start < len(text):
        piece = text[start : start + maximum]
        if piece:
            pieces.append(piece)
        if start + maximum >= len(text):
            break
        start += step
    return pieces


def _paragraph_pieces(text: str, maximum: int) -> list[tuple[str | None, str]]:
    pieces: list[tuple[str | None, str]] = []
    for paragraph in re.split(r"\n\s*\n", text):
        paragraph = paragraph.strip()
        if not paragraph:
            continue
        for piece in _fixed_pieces(paragraph, maximum, 0):
            pieces.append((None, piece))
    return pieces


def _heading_title(line: str) -> str | None:
    for pattern in (_MARKDOWN_HEADING, _PLAIN_HEADING):
        match = pattern.match(line)
        if match:
            return match.group("title").strip()
    return None


def _heading_pieces(text: str, maximum: int) -> list[tuple[str | None, str]]:
    sections: list[tuple[str | None, str]] = []
    current_title: str | None = None
    current_lines: list[str] = []

    def flush() -> None:
        if not current_lines:
            return
        section_text = "\n".join(current_lines).strip()
        for piece in _fixed_pieces(section_text, maximum, 0):
            sections.append((current_title, piece))

    for line in text.splitlines():
        title = _heading_title(line)
        if title is not None:
            flush()
            current_title = title
            current_lines = [line]
        else:
            current_lines.append(line)
    flush()
    return sections


def _structured_field(line: str) -> tuple[str, str] | None:
    candidate = re.sub(r"^\s{0,3}#{1,6}\s*", "", line).strip()
    for field_name, aliases in _STRUCTURED_NAMES:
        for alias in aliases:
            match = re.match(
                rf"^{re.escape(alias)}\s*[:：]?\s*(?P<inline>.*)$", candidate
            )
            if match:
                return field_name, match.group("inline").strip()
    return None


def _structured_text_pieces(text: str) -> list[tuple[str | None, str]]:
    sections: list[tuple[str | None, str]] = []
    current_field: str | None = None
    current_lines: list[str] = []

    def flush() -> None:
        if current_field is None:
            return
        body = "\n".join(current_lines).strip()
        if body:
            label = _STRUCTURED_LABELS[current_field]
            sections.append((label, f"{label}\n{body}"))

    for line in text.splitlines():
        field_match = _structured_field(line)
        if field_match is not None:
            flush()
            current_field, inline_text = field_match
            current_lines = [inline_text] if inline_text else []
        elif current_field is not None:
            current_lines.append(line)
    flush()
    return sections


def _structured_pieces(document: Document, text: str) -> list[tuple[str | None, str]]:
    metadata_values = (
        ("question", document.metadata.question),
        ("cause", document.metadata.cause),
        ("resolution", document.metadata.resolution),
        ("manufacturer_reply", document.metadata.manufacturer_reply),
        ("customer_final_reply", document.metadata.customer_final_reply),
    )
    populated = [(name, value) for name, value in metadata_values if value]
    if populated:
        return [
            (_STRUCTURED_LABELS[name], f"{_STRUCTURED_LABELS[name]}\n{value.strip()}")
            for name, value in populated
        ]

    parsed = _structured_text_pieces(text)
    return parsed if parsed else [(None, text)] if text else []


def _stable_chunk_id(
    document_id: str,
    strategy: ChunkingStrategy,
    index: int,
    section_title: str | None,
    text: str,
) -> str:
    material = "\x1f".join(
        (document_id, strategy.value, str(index), section_title or "", text)
    )
    digest = hashlib.sha256(material.encode("utf-8")).hexdigest()[:20]
    return f"{document_id}:{strategy.value}:{digest}"


def chunk_document(
    document: Document, options: ChunkingOptions | None = None
) -> list[Chunk]:
    options = options or ChunkingOptions()
    text = normalize_text(document.text, options.normalization).text

    if options.strategy is ChunkingStrategy.FIXED:
        pieces = [
            (None, piece)
            for piece in _fixed_pieces(
                text, options.max_characters, options.overlap_characters
            )
        ]
    elif options.strategy is ChunkingStrategy.PARAGRAPH:
        pieces = _paragraph_pieces(text, options.max_characters)
    elif options.strategy is ChunkingStrategy.HEADING:
        pieces = _heading_pieces(text, options.max_characters)
    elif options.strategy is ChunkingStrategy.STRUCTURED:
        raw_pieces = _structured_pieces(document, text)
        pieces = []
        for title, raw_piece in raw_pieces:
            pieces.extend(
                (title, piece)
                for piece in _fixed_pieces(raw_piece, options.max_characters, 0)
            )
    else:
        raise ValueError(f"unsupported chunking strategy: {options.strategy}")

    return [
        Chunk(
            chunk_id=_stable_chunk_id(
                document.document_id, options.strategy, index, title, piece
            ),
            document_id=document.document_id,
            chunk_index=index,
            strategy=options.strategy.value,
            text=piece,
            metadata=document.metadata,
            section_title=title,
        )
        for index, (title, piece) in enumerate(pieces)
        if piece
    ]
