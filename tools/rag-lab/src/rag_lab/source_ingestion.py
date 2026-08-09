from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import PurePosixPath
from typing import Iterable


_DRIVE_PATH = re.compile(r"^[A-Za-z]:")


@dataclass(frozen=True, slots=True)
class SourceIngestionObservation:
    case_id: str
    expected_included: bool
    actual_included: bool
    source_path: str
    sha256: str = ""

    @property
    def is_archive_entry(self) -> bool:
        return "!/" in self.source_path


@dataclass(frozen=True, slots=True)
class SourceIngestionEvaluation:
    false_positive_ids: tuple[str, ...]
    false_negative_ids: tuple[str, ...]
    duplicate_content_groups: tuple[tuple[str, ...], ...]
    duplicate_index_violations: tuple[tuple[str, ...], ...]
    external_preference_violations: tuple[str, ...]
    invalid_archive_source_ids: tuple[str, ...]

    @property
    def passed(self) -> bool:
        return not (
            self.false_positive_ids
            or self.false_negative_ids
            or self.duplicate_index_violations
            or self.external_preference_violations
            or self.invalid_archive_source_ids
        )


def evaluate_source_ingestion(
    observations: Iterable[SourceIngestionObservation],
) -> SourceIngestionEvaluation:
    values = tuple(observations)
    false_positives = tuple(sorted(item.case_id for item in values if item.actual_included and not item.expected_included))
    false_negatives = tuple(sorted(item.case_id for item in values if item.expected_included and not item.actual_included))

    hashes: dict[str, list[SourceIngestionObservation]] = {}
    for item in values:
        if item.sha256:
            hashes.setdefault(item.sha256.casefold(), []).append(item)
    duplicate_groups = tuple(
        tuple(sorted(item.case_id for item in group))
        for _, group in sorted(hashes.items())
        if len(group) > 1
    )
    duplicate_violations = tuple(
        tuple(sorted(item.case_id for item in group if item.actual_included))
        for _, group in sorted(hashes.items())
        if sum(item.actual_included for item in group) > 1
    )
    external_preference_violations = tuple(
        sorted(
            sha
            for sha, group in hashes.items()
            if any(not item.is_archive_entry for item in group)
            and any(item.is_archive_entry for item in group)
            and not any(item.actual_included and not item.is_archive_entry for item in group)
        )
    )
    invalid_archive_sources = tuple(
        sorted(
            item.case_id
            for item in values
            if item.is_archive_entry and not is_valid_archive_source_path(item.source_path)
        )
    )
    return SourceIngestionEvaluation(
        false_positive_ids=false_positives,
        false_negative_ids=false_negatives,
        duplicate_content_groups=duplicate_groups,
        duplicate_index_violations=duplicate_violations,
        external_preference_violations=external_preference_violations,
        invalid_archive_source_ids=invalid_archive_sources,
    )


def is_valid_archive_source_path(source_path: str) -> bool:
    archive_path, separator, entry_path = source_path.partition("!/")
    if not separator or not archive_path.casefold().endswith(".zip") or not entry_path:
        return False
    normalized = entry_path.replace("\\", "/")
    if normalized.startswith("/") or normalized.startswith("//") or _DRIVE_PATH.match(normalized):
        return False
    return not any(part in {".", ".."} for part in PurePosixPath(normalized).parts)
