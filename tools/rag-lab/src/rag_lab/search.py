from __future__ import annotations

import math
import re
import unicodedata
from collections import Counter
from dataclasses import dataclass
from enum import StrEnum
from typing import Iterable, Protocol

from .models import Chunk


class SearchMethod(StrEnum):
    KEYWORD = "keyword"
    BM25 = "bm25"


@dataclass(frozen=True, slots=True)
class SearchFilters:
    product_name: str | None = None
    target_version: str | None = None


@dataclass(frozen=True, slots=True)
class SearchResult:
    chunk: Chunk
    score: float
    rank: int
    matched_terms: tuple[str, ...] = ()

    def to_public_dict(self) -> dict[str, object]:
        return {
            "rank": self.rank,
            "score": round(self.score, 8),
            "matched_terms": list(self.matched_terms),
            "chunk": self.chunk.to_public_dict(),
        }


class SearchIndex(Protocol):
    method: SearchMethod

    def search(
        self,
        query: str,
        *,
        top_k: int = 5,
        filters: SearchFilters | None = None,
    ) -> list[SearchResult]: ...


_TOKEN_PATTERN = re.compile(
    r"[A-Za-z0-9][A-Za-z0-9_.+#/:-]*|[一-龯々ぁ-んァ-ヶー]+"
)
_JAPANESE_PATTERN = re.compile(r"[一-龯々ぁ-んァ-ヶー]")


def tokenize(text: str) -> list[str]:
    normalized = unicodedata.normalize("NFKC", text).casefold()
    terms: list[str] = []
    for match in _TOKEN_PATTERN.finditer(normalized):
        token = match.group(0)
        terms.append(token)
        if _JAPANESE_PATTERN.search(token):
            for width in (2, 3):
                if len(token) >= width:
                    terms.extend(
                        token[index : index + width]
                        for index in range(len(token) - width + 1)
                    )
    return terms


def _normalized_value(value: str | None) -> str | None:
    if value is None:
        return None
    return unicodedata.normalize("NFKC", value).strip().casefold()


def _matches_filters(chunk: Chunk, filters: SearchFilters | None) -> bool:
    if filters is None:
        return True
    if filters.product_name is not None and _normalized_value(
        chunk.metadata.product_name
    ) != _normalized_value(filters.product_name):
        return False
    if filters.target_version is not None and _normalized_value(
        chunk.metadata.target_version
    ) != _normalized_value(filters.target_version):
        return False
    return True


def _searchable_text(chunk: Chunk) -> str:
    metadata_values = (
        chunk.metadata.product_name,
        chunk.metadata.support_id,
        chunk.metadata.information_type,
        chunk.metadata.document_name,
        chunk.metadata.target_version,
        chunk.metadata.os,
        chunk.metadata.error_code,
        chunk.metadata.command,
        chunk.metadata.source_type,
    )
    return "\n".join((chunk.text, *(value for value in metadata_values if value)))


@dataclass(frozen=True, slots=True)
class _IndexedChunk:
    chunk: Chunk
    searchable_text: str
    term_counts: Counter[str]
    length: int


class _BaseSearchIndex:
    method: SearchMethod

    def __init__(self, chunks: Iterable[Chunk]) -> None:
        self._documents = tuple(
            _IndexedChunk(
                chunk=chunk,
                searchable_text=(searchable := _searchable_text(chunk)),
                term_counts=(counts := Counter(tokenize(searchable))),
                length=sum(counts.values()),
            )
            for chunk in chunks
        )

    def _score(
        self, query: str, query_counts: Counter[str], document: _IndexedChunk
    ) -> float:
        raise NotImplementedError

    def search(
        self,
        query: str,
        *,
        top_k: int = 5,
        filters: SearchFilters | None = None,
    ) -> list[SearchResult]:
        if top_k <= 0:
            raise ValueError("top_k must be greater than zero")
        query_counts = Counter(tokenize(query))
        if not query_counts:
            return []

        scored: list[tuple[float, _IndexedChunk, tuple[str, ...]]] = []
        for document in self._documents:
            if not _matches_filters(document.chunk, filters):
                continue
            score = self._score(query, query_counts, document)
            if score <= 0:
                continue
            matched = tuple(
                sorted(term for term in query_counts if term in document.term_counts)
            )
            scored.append((score, document, matched))

        scored.sort(
            key=lambda item: (
                -item[0],
                item[1].chunk.document_id,
                item[1].chunk.chunk_index,
            )
        )
        return [
            SearchResult(document.chunk, score, rank, matched)
            for rank, (score, document, matched) in enumerate(
                scored[:top_k], start=1
            )
        ]


class KeywordSearchIndex(_BaseSearchIndex):
    method = SearchMethod.KEYWORD

    def _score(
        self, query: str, query_counts: Counter[str], document: _IndexedChunk
    ) -> float:
        unique_query_terms = set(query_counts)
        matched_terms = unique_query_terms.intersection(document.term_counts)
        if not matched_terms:
            return 0.0
        coverage = len(matched_terms) / len(unique_query_terms)
        frequency_bonus = sum(
            math.log1p(document.term_counts[term]) for term in matched_terms
        ) / max(1, len(unique_query_terms))
        normalized_query = unicodedata.normalize("NFKC", query).strip().casefold()
        phrase_bonus = (
            0.25
            if normalized_query
            and normalized_query
            in unicodedata.normalize("NFKC", document.searchable_text).casefold()
            else 0.0
        )
        return coverage + (0.05 * frequency_bonus) + phrase_bonus


class BM25SearchIndex(_BaseSearchIndex):
    method = SearchMethod.BM25

    def __init__(
        self, chunks: Iterable[Chunk], *, k1: float = 1.5, b: float = 0.75
    ) -> None:
        super().__init__(chunks)
        if k1 <= 0:
            raise ValueError("k1 must be greater than zero")
        if not 0 <= b <= 1:
            raise ValueError("b must be between zero and one")
        self._k1 = k1
        self._b = b
        self._average_length = (
            sum(document.length for document in self._documents)
            / len(self._documents)
            if self._documents
            else 0.0
        )
        frequencies: Counter[str] = Counter()
        for document in self._documents:
            frequencies.update(document.term_counts.keys())
        total = len(self._documents)
        self._idf = {
            term: math.log(1.0 + ((total - count + 0.5) / (count + 0.5)))
            for term, count in frequencies.items()
        }

    def _score(
        self, query: str, query_counts: Counter[str], document: _IndexedChunk
    ) -> float:
        if self._average_length <= 0:
            return 0.0
        score = 0.0
        length_ratio = document.length / self._average_length
        for term, query_frequency in query_counts.items():
            term_frequency = document.term_counts.get(term, 0)
            if term_frequency == 0:
                continue
            denominator = term_frequency + self._k1 * (
                1.0 - self._b + self._b * length_ratio
            )
            score += (
                self._idf.get(term, 0.0)
                * ((term_frequency * (self._k1 + 1.0)) / denominator)
                * query_frequency
            )
        return score


def build_search_index(
    chunks: Iterable[Chunk], method: SearchMethod | str
) -> SearchIndex:
    selected = SearchMethod(method)
    if selected is SearchMethod.KEYWORD:
        return KeywordSearchIndex(chunks)
    if selected is SearchMethod.BM25:
        return BM25SearchIndex(chunks)
    raise ValueError(f"unsupported search method: {selected}")
