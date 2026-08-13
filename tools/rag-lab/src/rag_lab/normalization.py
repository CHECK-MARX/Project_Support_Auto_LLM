from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass, field


KNOWN_CATEGORIES = frozenset(
    {
        "greeting",
        "signature",
        "webinar",
        "quoted_mail",
        "separator",
        "generated_header",
    }
)

_GREETING = re.compile(
    r"^(?:いつも)?お世話になっております[。.]?$|^Dear\s+[^,]+[,]?$",
    re.IGNORECASE,
)
_SIGNATURE_START = re.compile(r"^(?:署名|signature)\s*[:：]?$", re.IGNORECASE)
_WEBINAR = re.compile(r"(?:オンラインセミナー|ウェビナー|\bwebinar\b)", re.IGNORECASE)
_QUOTED_MAIL_START = re.compile(
    r"^(?:-{2,}\s*Original Message\s*-{2,}|-{2,}\s*転送メッセージ\s*-{2,})$",
    re.IGNORECASE,
)
_SEPARATOR = re.compile(r"^\s*[-=_*]{5,}\s*$")
_GENERATED_HEADER = re.compile(
    r"^\s*\*{3,}.*(?:追記部|受付|調査中|回答済み).*\*{3,}\s*$"
)


@dataclass(frozen=True, slots=True)
class NormalizationOptions:
    unicode_nfkc: bool = True
    trim_trailing_whitespace: bool = True
    max_consecutive_blank_lines: int = 1
    exclude_categories: frozenset[str] = field(default_factory=frozenset)

    def __post_init__(self) -> None:
        if self.max_consecutive_blank_lines < 0:
            raise ValueError("max_consecutive_blank_lines must be zero or greater")
        unknown = self.exclude_categories - KNOWN_CATEGORIES
        if unknown:
            raise ValueError(f"unknown normalization categories: {sorted(unknown)}")


@dataclass(frozen=True, slots=True)
class ExcludedSegment:
    category: str
    text: str


@dataclass(frozen=True, slots=True)
class NormalizationResult:
    text: str
    excluded_segments: tuple[ExcludedSegment, ...] = ()


def _classify_line(line: str, active_block: str | None) -> tuple[str | None, str | None]:
    stripped = line.strip()
    if active_block is not None:
        return active_block, active_block
    if _SIGNATURE_START.match(stripped):
        return "signature", "signature"
    if _QUOTED_MAIL_START.match(stripped):
        return "quoted_mail", "quoted_mail"
    if stripped.startswith(">"):
        return "quoted_mail", None
    if _GREETING.match(stripped):
        return "greeting", None
    if _WEBINAR.search(stripped):
        return "webinar", None
    if _GENERATED_HEADER.match(stripped):
        return "generated_header", None
    if _SEPARATOR.match(stripped):
        return "separator", None
    return None, None


def _collapse_blank_lines(lines: list[str], maximum: int) -> list[str]:
    result: list[str] = []
    blank_count = 0
    for line in lines:
        if line:
            blank_count = 0
            result.append(line)
            continue
        blank_count += 1
        if blank_count <= maximum:
            result.append(line)
    return result


def normalize_text(
    text: str, options: NormalizationOptions | None = None
) -> NormalizationResult:
    if not isinstance(text, str):
        raise TypeError("text must be a string")

    options = options or NormalizationOptions()
    normalized = text.replace("\r\n", "\n").replace("\r", "\n")
    if options.unicode_nfkc:
        normalized = unicodedata.normalize("NFKC", normalized)

    kept_lines: list[str] = []
    removed: list[ExcludedSegment] = []
    active_block: str | None = None

    for original_line in normalized.split("\n"):
        line = original_line.rstrip() if options.trim_trailing_whitespace else original_line
        category, new_active_block = _classify_line(line, active_block)
        if (
            new_active_block is not None
            and new_active_block in options.exclude_categories
        ):
            active_block = new_active_block

        if category in options.exclude_categories:
            if removed and removed[-1].category == category:
                previous = removed[-1]
                removed[-1] = ExcludedSegment(category, f"{previous.text}\n{line}")
            else:
                removed.append(ExcludedSegment(category, line))
            continue

        kept_lines.append(line)

    kept_lines = _collapse_blank_lines(
        kept_lines, options.max_consecutive_blank_lines
    )
    return NormalizationResult(
        text="\n".join(kept_lines).strip("\n"),
        excluded_segments=tuple(removed),
    )
