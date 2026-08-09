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
from .coverage_set_selection import (
    CoverageEvidenceCandidate,
    CoverageEvidenceSelectionRequest,
    CoverageEvidenceSelectionResult,
    CoverageEvidenceSelectionDecision,
    select_coverage_evidence,
)
from .reranking import Reranker, RerankerMethod, apply_reranker
from .quality import QualityGateThresholds, apply_quality_gate
from .regression import RegressionThresholds, compare_reports
from .search import SearchFilters, SearchMethod, SearchResult, build_search_index
from .source_ingestion import (
    SourceIngestionEvaluation,
    SourceIngestionObservation,
    evaluate_source_ingestion,
    is_valid_archive_source_path,
)

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
    "SourceIngestionEvaluation",
    "SourceIngestionObservation",
    "build_search_index",
    "evaluate_source_ingestion",
    "is_valid_archive_source_path",
    "apply_reranker",
    "apply_quality_gate",
    "compare_reports",
    "build_codex_evidence",
    "CoverageEvidenceCandidate",
    "CoverageEvidenceSelectionRequest",
    "CoverageEvidenceSelectionResult",
    "CoverageEvidenceSelectionDecision",
    "select_coverage_evidence",
    "chunk_document",
    "normalize_text",
]
