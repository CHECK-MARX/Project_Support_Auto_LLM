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
from .quality import QualityGateThresholds, apply_quality_gate
from .regression import RegressionThresholds, compare_reports
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
    "QualityGateThresholds",
    "RegressionThresholds",
    "SearchFilters",
    "SearchMethod",
    "SearchResult",
    "build_search_index",
    "apply_reranker",
    "apply_quality_gate",
    "compare_reports",
    "build_codex_evidence",
    "chunk_document",
    "normalize_text",
]
