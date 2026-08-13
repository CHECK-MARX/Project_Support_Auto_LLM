from __future__ import annotations

from dataclasses import dataclass
from typing import Sequence

from rag_lab.embedding import (
    EmbeddingSearchIndex,
    HashingEmbeddingProvider,
    HybridSearchIndex,
    Vector,
)
from rag_lab.models import Chunk, DocumentMetadata
from rag_lab.search import SearchFilters, SearchMethod, build_search_index


def _chunk(document_id: str, text: str, product: str = "Product") -> Chunk:
    return Chunk(
        chunk_id=f"{document_id}:0",
        document_id=document_id,
        chunk_index=0,
        strategy="test",
        text=text,
        metadata=DocumentMetadata(product_name=product),
    )


def test_hashing_embedding_is_deterministic_and_normalized() -> None:
    provider = HashingEmbeddingProvider(dimensions=64)

    first, second = provider.embed(["CCTの生成方法", "CCTの生成方法"])

    assert first == second
    assert len(first) == 64
    assert abs(sum(value * value for value in first) - 1.0) < 1e-9


@dataclass(frozen=True)
class FakeEmbeddingProvider:
    dimensions: int = 2
    provider_id: str = "test-provider"

    def embed(self, texts: Sequence[str]) -> list[Vector]:
        return [
            (1.0, 0.0) if "target" in text.casefold() else (0.0, 1.0)
            for text in texts
        ]


def test_embedding_provider_can_be_replaced_without_index_changes() -> None:
    index = EmbeddingSearchIndex(
        [_chunk("target", "target procedure"), _chunk("other", "other topic")],
        FakeEmbeddingProvider(),
    )

    results = index.search("target question", top_k=2)

    assert results[0].chunk.document_id == "target"


def test_hybrid_search_honors_metadata_filter() -> None:
    index = HybridSearchIndex(
        [
            _chunk("qac", "共通エラーの対処", "HelixQAC"),
            _chunk("checkmarx", "共通エラーの対処", "Checkmarx"),
        ],
        provider=HashingEmbeddingProvider(dimensions=64),
    )

    results = index.search(
        "共通エラー",
        top_k=5,
        filters=SearchFilters(product_name="Checkmarx"),
    )

    assert [result.chunk.document_id for result in results] == ["checkmarx"]


def test_search_factory_exposes_offline_embedding_and_hybrid_methods() -> None:
    chunks = [_chunk("doc", "CCT生成方法")]

    embedding = build_search_index(chunks, SearchMethod.HASH_EMBEDDING)
    hybrid = build_search_index(chunks, SearchMethod.HYBRID)

    assert embedding.method is SearchMethod.HASH_EMBEDDING
    assert hybrid.method is SearchMethod.HYBRID
