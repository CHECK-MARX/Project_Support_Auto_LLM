from __future__ import annotations

import unicodedata
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Iterable

from .io import LabPaths, write_json_report
from .search import SearchResult


@dataclass(frozen=True, slots=True)
class EvidenceSelectionOptions:
    top_k: int = 3
    stale_after_days: int = 730

    def __post_init__(self) -> None:
        if not 1 <= self.top_k <= 5:
            raise ValueError("evidence top_k must be between 1 and 5")
        if self.stale_after_days <= 0:
            raise ValueError("stale_after_days must be greater than zero")


def _normalized(value: str | None) -> str | None:
    if value is None:
        return None
    return unicodedata.normalize("NFKC", value).strip().casefold()


def _parse_datetime(value: str | None) -> datetime | None:
    if not value:
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=UTC)
    return parsed.astimezone(UTC)


def _deduplicate(results: Iterable[SearchResult]) -> list[SearchResult]:
    selected: list[SearchResult] = []
    seen: set[str] = set()
    for result in results:
        if result.chunk.document_id in seen:
            continue
        seen.add(result.chunk.document_id)
        selected.append(result)
    return selected


def _conflict_indexes(results: list[SearchResult]) -> set[int]:
    conflicts: set[int] = set()
    for left_index, left in enumerate(results):
        left_product = _normalized(left.chunk.metadata.product_name)
        left_version = _normalized(left.chunk.metadata.target_version)
        if left_product is None or left_version is None:
            continue
        for right_index in range(left_index + 1, len(results)):
            right = results[right_index]
            if (
                _normalized(right.chunk.metadata.product_name) == left_product
                and (right_version := _normalized(right.chunk.metadata.target_version))
                is not None
                and right_version != left_version
            ):
                conflicts.update((left_index, right_index))
    return conflicts


def build_codex_evidence(
    query: str,
    results: Iterable[SearchResult],
    *,
    product: str | None = None,
    target_version: str | None = None,
    options: EvidenceSelectionOptions | None = None,
    now: datetime | None = None,
) -> dict[str, object]:
    options = options or EvidenceSelectionOptions()
    selected = _deduplicate(results)[: options.top_k]
    conflicts = _conflict_indexes(selected)
    current = (now or datetime.now(UTC)).astimezone(UTC)
    expected_product = _normalized(product)
    expected_version = _normalized(target_version)
    evidence_items: list[dict[str, object]] = []

    for index, result in enumerate(selected):
        metadata = result.chunk.metadata
        actual_product = _normalized(metadata.product_name)
        actual_version = _normalized(metadata.target_version)
        product_match = (
            actual_product == expected_product if expected_product is not None else None
        )
        version_match = (
            actual_version == expected_version if expected_version is not None else None
        )
        source_date = _parse_datetime(metadata.created_or_updated_at)
        stale = (
            source_date < current - timedelta(days=options.stale_after_days)
            if source_date is not None
            else None
        )
        possible_conflict = index in conflicts
        matched_terms = sorted(
            set(result.matched_terms), key=lambda term: (-len(term), term)
        )[:20]
        warnings: list[str] = []
        unverified: list[str] = []
        if product_match is False:
            warnings.append("対象製品と根拠の製品が一致しません。")
        if version_match is False:
            warnings.append("対象バージョンと根拠のバージョンが一致しません。")
        if stale is True:
            warnings.append("根拠が設定された鮮度基準より古い可能性があります。")
        if possible_conflict:
            warnings.append("同一製品の異なるバージョンの根拠が含まれています。")
        if metadata.target_version is None:
            unverified.append("根拠の対象バージョン")
        if source_date is None:
            unverified.append("根拠の作成日または更新日")
        if metadata.confidence is None:
            unverified.append("根拠の信頼度")
        selection_reason = (
            f"検索順位{result.rank}位、スコア{result.score:.6f}。"
            f"製品一致={product_match if product_match is not None else '未指定'}、"
            f"バージョン一致={version_match if version_match is not None else '未指定'}、"
            f"キーワード一致={len(matched_terms)}件。"
        )
        evidence_items.append(
            {
                "sourceType": metadata.source_type or "Unknown",
                "documentId": result.chunk.document_id,
                "supportId": metadata.support_id,
                "product": metadata.product_name,
                "version": metadata.target_version,
                "score": round(result.score, 8),
                "selectionReason": selection_reason,
                "warnings": warnings,
                "text": result.chunk.text,
                "productMatch": product_match,
                "versionMatch": version_match,
                "keywordMatches": matched_terms,
                "possiblyStale": stale,
                "possibleConflict": possible_conflict,
                "unverifiedItems": unverified,
            }
        )
    return {"query": query, "selectedEvidence": evidence_items}


def write_codex_evidence(
    paths: LabPaths, relative_path: str | Path, payload: dict[str, object]
) -> Path:
    return write_json_report(paths, relative_path, payload)
