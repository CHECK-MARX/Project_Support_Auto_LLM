"""Independent, offline RAG evaluation primitives for SupportCaseManager."""

from .chunking import ChunkingOptions, ChunkingStrategy, chunk_document
from .models import Chunk, Document, DocumentMetadata, EvaluationCase
from .normalization import (
    ExcludedSegment,
    NormalizationOptions,
    NormalizationResult,
    normalize_text,
)
from .embedding import EmbeddingProvider, HashingEmbeddingProvider
from .evidence import EvidenceSelectionOptions, build_codex_evidence
from .reranking import Reranker, RerankerMethod, apply_reranker
from .search import SearchFilters, SearchMethod, SearchResult, build_search_index

__all__ = [
    "Chunk",
    "ChunkingOptions",
    "ChunkingStrategy",
    "Document",
    "DocumentMetadata",
    "EvaluationCase",
    "EmbeddingProvider",
    "EvidenceSelectionOptions",
    "ExcludedSegment",
    "NormalizationOptions",
    "NormalizationResult",
    "HashingEmbeddingProvider",
    "Reranker",
    "RerankerMethod",
    "SearchFilters",
    "SearchMethod",
    "SearchResult",
    "build_search_index",
    "apply_reranker",
    "build_codex_evidence",
    "chunk_document",
    "normalize_text",
]
