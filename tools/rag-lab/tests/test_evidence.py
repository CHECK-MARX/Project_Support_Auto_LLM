from datetime import UTC, datetime
from pathlib import Path

import pytest

from rag_lab.evidence import EvidenceSelectionOptions, build_codex_evidence
from rag_lab.models import Chunk, DocumentMetadata
from rag_lab.search import SearchResult


def _result(
    document_id: str,
    rank: int,
    *,
    version: str | None,
    updated_at: str | None,
) -> SearchResult:
    chunk = Chunk(
        chunk_id=f"{document_id}:{rank}",
        document_id=document_id,
        chunk_index=rank - 1,
        strategy="heading",
        text=f"根拠本文 {document_id}",
        metadata=DocumentMetadata(
            product_name="Checkmarx",
            support_id="SYN-1",
            source_path=rf"C:\private\{document_id}.txt",
            source_type="SyntheticManual",
            target_version=version,
            created_or_updated_at=updated_at,
        ),
    )
    return SearchResult(
        chunk=chunk,
        score=1.0 / rank,
        rank=rank,
        matched_terms=("Path Traversal", "Not Exploitable"),
    )


def test_codex_evidence_is_deduplicated_redacted_and_warns_about_risk() -> None:
    results = [
        _result("doc-1", 1, version="1.0", updated_at="2020-01-01T00:00:00Z"),
        _result("doc-1", 2, version="1.0", updated_at="2020-01-01T00:00:00Z"),
        _result("doc-2", 3, version="2.0", updated_at=None),
    ]

    payload = build_codex_evidence(
        "Path Traversalの判定",
        results,
        product="Checkmarx",
        target_version="2.0",
        options=EvidenceSelectionOptions(top_k=3, stale_after_days=365),
        now=datetime(2026, 1, 1, tzinfo=UTC),
    )

    selected = payload["selectedEvidence"]
    assert isinstance(selected, list)
    assert len(selected) == 2
    assert selected[0]["documentId"] == "doc-1"
    assert selected[0]["possiblyStale"] is True
    assert selected[0]["versionMatch"] is False
    assert selected[0]["possibleConflict"] is True
    assert selected[1]["possibleConflict"] is True
    assert "sourcePath" not in str(payload)
    assert r"C:\private" not in str(payload)
    assert selected[0]["selectionReason"]
    assert selected[0]["keywordMatches"]


@pytest.mark.parametrize("top_k", [0, 6])
def test_evidence_count_is_limited_to_one_through_five(top_k: int) -> None:
    with pytest.raises(ValueError, match="between 1 and 5"):
        EvidenceSelectionOptions(top_k=top_k)
