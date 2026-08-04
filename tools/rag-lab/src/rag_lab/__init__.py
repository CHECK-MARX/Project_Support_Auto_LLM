"""Independent, offline RAG evaluation primitives for SupportCaseManager."""

from .chunking import ChunkingOptions, ChunkingStrategy, chunk_document
from .models import Chunk, Document, DocumentMetadata, EvaluationCase
from .normalization import (
    ExcludedSegment,
    NormalizationOptions,
    NormalizationResult,
    normalize_text,
)
from .search import SearchFilters, SearchMethod, SearchResult, build_search_index

__all__ = [
    "Chunk",
    "ChunkingOptions",
    "ChunkingStrategy",
    "Document",
    "DocumentMetadata",
    "EvaluationCase",
    "ExcludedSegment",
    "NormalizationOptions",
    "NormalizationResult",
    "SearchFilters",
    "SearchMethod",
    "SearchResult",
    "build_search_index",
    "chunk_document",
    "normalize_text",
]
