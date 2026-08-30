from __future__ import annotations

import hashlib
import json
import math
from collections import Counter
from dataclasses import dataclass
from typing import Iterable, Protocol, Sequence
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from .models import Chunk
from .search import (
    BM25SearchIndex,
    SearchFilters,
    SearchMethod,
    SearchResult,
    matches_filters,
    searchable_text,
    tokenize,
)


Vector = tuple[float, ...]


class EmbeddingProvider(Protocol):
    """Replaceable local embedding provider. Implementations must not log input text."""

    provider_id: str
    dimensions: int

    def embed(self, texts: Sequence[str]) -> list[Vector]: ...


@dataclass(frozen=True, slots=True)
class HashingEmbeddingProvider:
    """Deterministic offline baseline; this is not a semantic language model."""

    dimensions: int = 256
    provider_id: str = "offline-token-hash-v1"

    def __post_init__(self) -> None:
        if self.dimensions < 16:
            raise ValueError("dimensions must be at least 16")

    def embed(self, texts: Sequence[str]) -> list[Vector]:
        return [self._embed_one(text) for text in texts]

    def _embed_one(self, text: str) -> Vector:
        values = [0.0] * self.dimensions
        for term, count in Counter(tokenize(text)).items():
            digest = hashlib.sha256(term.encode("utf-8")).digest()
            index = int.from_bytes(digest[:8], "big") % self.dimensions
            sign = 1.0 if digest[8] & 1 else -1.0
            values[index] += sign * (1.0 + math.log(count))
        norm = math.sqrt(sum(value * value for value in values))
        if norm == 0:
            return tuple(values)
        return tuple(value / norm for value in values)


@dataclass(slots=True)
class OllamaEmbeddingProvider:
    """Local Ollama provider used only by RAG Lab A/B evaluation.

    Inputs are sent only to the configured local endpoint and are never logged.
    """

    model: str
    endpoint: str = "http://localhost:11434"
    timeout_seconds: float = 30.0
    provider_id: str = "ollama"
    dimensions: int = 0

    def embed(self, texts: Sequence[str]) -> list[Vector]:
        if not texts:
            return []
        request = Request(
            f"{self.endpoint.rstrip('/')}/api/embed",
            data=json.dumps({"model": self.model, "input": list(texts)}).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urlopen(request, timeout=self.timeout_seconds) as response:  # noqa: S310 local configured endpoint
                payload = json.loads(response.read().decode("utf-8"))
        except HTTPError as error:
            detail = error.read().decode("utf-8", errors="replace")[:400]
            raise RuntimeError(f"Ollama embedding HTTP {error.code}: {detail}") from error
        except URLError as error:
            raise RuntimeError(f"Ollama embedding endpoint is unavailable: {error.reason}") from error
        vectors = payload.get("embeddings") if isinstance(payload, dict) else None
        if not isinstance(vectors, list) or len(vectors) != len(texts):
            raise RuntimeError("Ollama returned an invalid embedding count")
        result = [tuple(float(value) for value in vector) for vector in vectors]
        if not result or not result[0] or any(len(vector) != len(result[0]) for vector in result):
            raise RuntimeError("Ollama returned inconsistent embedding dimensions")
        self.dimensions = len(result[0])
        return [_normalize(vector) for vector in result]


def _normalize(vector: Vector) -> Vector:
    norm = math.sqrt(sum(value * value for value in vector))
    return vector if norm == 0 else tuple(value / norm for value in vector)


def _cosine(left: Vector, right: Vector) -> float:
    if len(left) != len(right):
        raise ValueError("embedding dimensions do not match")
    return sum(a * b for a, b in zip(left, right, strict=True))


class EmbeddingSearchIndex:
    method = SearchMethod.HASH_EMBEDDING

    def __init__(
        self, chunks: Iterable[Chunk], provider: EmbeddingProvider
    ) -> None:
        self.provider = provider
        self._chunks = tuple(chunks)
        texts = [searchable_text(chunk) for chunk in self._chunks]
        vectors = provider.embed(texts)
        if len(vectors) != len(self._chunks):
            raise ValueError("embedding provider returned an unexpected vector count")
        if any(len(vector) != provider.dimensions for vector in vectors):
            raise ValueError("embedding provider returned an unexpected dimension")
        self._vectors = tuple(vectors)

    def search(
        self,
        query: str,
        *,
        top_k: int = 5,
        filters: SearchFilters | None = None,
    ) -> list[SearchResult]:
        if top_k <= 0:
            raise ValueError("top_k must be greater than zero")
        query_vector = self.provider.embed([query])
        if len(query_vector) != 1 or len(query_vector[0]) != self.provider.dimensions:
            raise ValueError("embedding provider returned an invalid query vector")
        query_terms = set(tokenize(query))
        scored: list[tuple[float, Chunk, tuple[str, ...]]] = []
        for chunk, vector in zip(self._chunks, self._vectors, strict=True):
            if not matches_filters(chunk, filters):
                continue
            score = _cosine(query_vector[0], vector)
            if score <= 0:
                continue
            matched = tuple(sorted(query_terms.intersection(tokenize(searchable_text(chunk)))))
            scored.append((score, chunk, matched))
        scored.sort(
            key=lambda item: (-item[0], item[1].document_id, item[1].chunk_index)
        )
        return [
            SearchResult(chunk, score, rank, matched)
            for rank, (score, chunk, matched) in enumerate(scored[:top_k], start=1)
        ]


class HybridSearchIndex:
    method = SearchMethod.HYBRID

    def __init__(
        self,
        chunks: Iterable[Chunk],
        *,
        provider: EmbeddingProvider | None = None,
        rrf_constant: int = 60,
    ) -> None:
        materialized = tuple(chunks)
        if rrf_constant <= 0:
            raise ValueError("rrf_constant must be greater than zero")
        self._candidate_count = max(1, len(materialized))
        self._rrf_constant = rrf_constant
        self._bm25 = BM25SearchIndex(materialized)
        self._embedding = EmbeddingSearchIndex(
            materialized, provider or HashingEmbeddingProvider()
        )

    def search(
        self,
        query: str,
        *,
        top_k: int = 5,
        filters: SearchFilters | None = None,
    ) -> list[SearchResult]:
        if top_k <= 0:
            raise ValueError("top_k must be greater than zero")
        bm25 = self._bm25.search(
            query, top_k=self._candidate_count, filters=filters
        )
        embedding = self._embedding.search(
            query, top_k=self._candidate_count, filters=filters
        )
        fused: dict[str, tuple[float, Chunk, set[str]]] = {}
        for results in (bm25, embedding):
            for result in results:
                key = result.chunk.chunk_id
                score, chunk, terms = fused.get(
                    key, (0.0, result.chunk, set())
                )
                score += 1.0 / (self._rrf_constant + result.rank)
                terms.update(result.matched_terms)
                fused[key] = (score, chunk, terms)
        ranked = sorted(
            fused.values(),
            key=lambda item: (-item[0], item[1].document_id, item[1].chunk_index),
        )
        return [
            SearchResult(chunk, score, rank, tuple(sorted(terms)))
            for rank, (score, chunk, terms) in enumerate(ranked[:top_k], start=1)
        ]
