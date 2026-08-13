from __future__ import annotations

import unicodedata
from dataclasses import dataclass
from enum import StrEnum
from typing import Protocol, Sequence

from .search import SearchFilters, SearchIndex, SearchResult, searchable_text, tokenize


class RerankerMethod(StrEnum):
    NONE = "none"
    LEXICAL = "lexical"


class Reranker(Protocol):
    reranker_id: str

    def rerank(
        self, query: str, candidates: Sequence[SearchResult], *, top_k: int
    ) -> list[SearchResult]: ...


@dataclass(frozen=True, slots=True)
class LexicalOverlapReranker:
    """Offline baseline reranker; replaceable with a local cross-encoder later."""

    reranker_id: str = "offline-lexical-overlap-v1"

    def rerank(
        self, query: str, candidates: Sequence[SearchResult], *, top_k: int
    ) -> list[SearchResult]:
        if top_k <= 0:
            raise ValueError("top_k must be greater than zero")
        if not candidates:
            return []
        query_terms = set(tokenize(query))
        normalized_query = unicodedata.normalize("NFKC", query).strip().casefold()
        maximum_base_score = max(candidate.score for candidate in candidates) or 1.0
        rescored: list[tuple[float, SearchResult, tuple[str, ...]]] = []
        for candidate in candidates:
            text = searchable_text(candidate.chunk)
            document_terms = set(tokenize(text))
            matched = tuple(sorted(query_terms.intersection(document_terms)))
            coverage = len(matched) / len(query_terms) if query_terms else 0.0
            phrase_bonus = (
                0.1
                if normalized_query
                and normalized_query
                in unicodedata.normalize("NFKC", text).casefold()
                else 0.0
            )
            base_score = candidate.score / maximum_base_score
            rescored.append(
                (0.7 * base_score + 0.3 * coverage + phrase_bonus, candidate, matched)
            )
        rescored.sort(
            key=lambda item: (
                -item[0],
                item[1].chunk.document_id,
                item[1].chunk.chunk_index,
            )
        )
        return [
            SearchResult(candidate.chunk, score, rank, matched)
            for rank, (score, candidate, matched) in enumerate(
                rescored[:top_k], start=1
            )
        ]


class RerankingSearchIndex:
    def __init__(
        self,
        base_index: SearchIndex,
        reranker: Reranker,
        *,
        candidate_multiplier: int = 4,
        minimum_candidates: int = 20,
    ) -> None:
        if candidate_multiplier <= 0 or minimum_candidates <= 0:
            raise ValueError("candidate limits must be greater than zero")
        self.method = base_index.method
        self.reranker = reranker
        self._base_index = base_index
        self._candidate_multiplier = candidate_multiplier
        self._minimum_candidates = minimum_candidates

    def search(
        self,
        query: str,
        *,
        top_k: int = 5,
        filters: SearchFilters | None = None,
    ) -> list[SearchResult]:
        if top_k <= 0:
            raise ValueError("top_k must be greater than zero")
        candidate_count = max(
            self._minimum_candidates, top_k * self._candidate_multiplier
        )
        candidates = self._base_index.search(
            query, top_k=candidate_count, filters=filters
        )
        return self.reranker.rerank(query, candidates, top_k=top_k)


def apply_reranker(
    index: SearchIndex, method: RerankerMethod | str
) -> SearchIndex:
    selected = RerankerMethod(method)
    if selected is RerankerMethod.NONE:
        return index
    if selected is RerankerMethod.LEXICAL:
        return RerankingSearchIndex(index, LexicalOverlapReranker())
    raise ValueError(f"unsupported reranker: {selected}")
