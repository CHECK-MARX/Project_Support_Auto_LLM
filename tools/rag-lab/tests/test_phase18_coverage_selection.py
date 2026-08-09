from __future__ import annotations

import json
from pathlib import Path

import pytest

from rag_lab.coverage_set_selection import (
    CoverageEvidenceCandidate,
    CoverageEvidenceSelectionRequest,
    select_coverage_evidence,
)


FIXTURE_PATH = (
    Path(__file__).resolve().parents[1]
    / "samples"
    / "phase18_coverage_selection_cases.json"
)
CASES = json.loads(FIXTURE_PATH.read_text(encoding="utf-8"))


@pytest.mark.parametrize("case", CASES, ids=lambda case: case["id"])
def test_shared_fixture_selected_ids_are_deterministic(case: dict[str, object]):
    request = _request(case)
    first = select_coverage_evidence(request)
    second = select_coverage_evidence(request)

    assert [item.candidate_id for item in first.selected] == case[
        "expectedSelectedIds"
    ]
    assert [item.candidate_id for item in first.decisions] == case[
        "expectedSelectedIds"
    ]
    assert first == second


def test_missing_search_and_selection_budget_are_distinct():
    missing_search = select_coverage_evidence(
        CoverageEvidenceSelectionRequest(
            required_coverage=("A", "B"),
            candidates=(_candidate("a", 1, ("A",), 0.8),),
        )
    )
    budget_limited = select_coverage_evidence(
        CoverageEvidenceSelectionRequest(
            required_coverage=("A", "B"),
            base_max_items=1,
            expansion_max_items=1,
            candidates=(
                _candidate("a", 1, ("A",), 0.9),
                _candidate("b", 2, ("B",), 0.8),
            ),
        )
    )

    assert "MissingCoverageInSearchResults" in missing_search.statuses
    assert "SelectionBudgetExceeded" not in missing_search.statuses
    assert "SelectionBudgetExceeded" in budget_limited.statuses


def test_corpus_gap_is_reported_only_with_corpus_coverage():
    unknown = select_coverage_evidence(
        CoverageEvidenceSelectionRequest(
            required_coverage=("A", "B"),
            candidates=(_candidate("a", 1, ("A",), 0.8),),
        )
    )
    known = select_coverage_evidence(
        CoverageEvidenceSelectionRequest(
            required_coverage=("A", "B"),
            corpus_coverage=("A",),
            candidates=(_candidate("a", 1, ("A",), 0.8),),
        )
    )

    assert "MissingCoverageInCorpus" not in unknown.statuses
    assert "MissingCoverageInCorpus" in known.statuses


def test_manual_items_are_never_removed_by_limit_or_budget():
    result = select_coverage_evidence(
        CoverageEvidenceSelectionRequest(
            required_coverage=("A",),
            base_max_items=1,
            expansion_max_items=1,
            character_budget=1,
            candidates=(
                _candidate("manual-1", 1, ("A",), 0.1, manual=True, chars=10),
                _candidate("manual-2", 2, (), 0.1, manual=True, chars=10),
            ),
        )
    )

    assert [item.candidate_id for item in result.selected] == [
        "manual-1",
        "manual-2",
    ]
    assert "ManualSelectionExceedsLimit" in result.warnings
    assert "ManualSelectionExceedsCharacterBudget" in result.warnings


def _request(case: dict[str, object]) -> CoverageEvidenceSelectionRequest:
    candidates = tuple(
        _fixture_candidate(item, index)
        for index, item in enumerate(case["candidates"], start=1)
    )
    return CoverageEvidenceSelectionRequest(
        required_coverage=tuple(case["requiredCoverage"]),
        candidates=candidates,
        base_max_items=int(case.get("baseMaxItems", 3)),
        expansion_max_items=int(case.get("expansionMaxItems", 5)),
        character_budget=int(case.get("characterBudget", 2000)),
        minimum_quality_score=float(case.get("minimumQualityScore", 0.30)),
    )


def _fixture_candidate(
    item: dict[str, object], default_rank: int
) -> CoverageEvidenceCandidate:
    quality = float(item.get("quality", 0.0))
    return CoverageEvidenceCandidate(
        candidate_id=str(item["id"]),
        original_rank=int(item.get("rank", default_rank)),
        source_type=str(item.get("sourceType", "Manual")),
        document_id=item.get("documentId"),
        file_path=item.get("filePath"),
        section=item.get("section"),
        text=str(item.get("text", item["id"])),
        content_hash=item.get("contentHash"),
        technical_tokens=tuple(item.get("technicalTokens", ())),
        coverage=tuple(item.get("coverage", ())),
        ranking_score=quality,
        topic_score=quality,
        entity_score=quality,
        technical_token_score=quality,
        source_trust=quality,
        version_score=quality,
        explicitly_excluded=bool(item.get("explicitlyExcluded", False)),
        topic_conflict=bool(item.get("topicConflict", False)),
        product_mismatch=bool(item.get("productMismatch", False)),
        manually_selected=bool(item.get("manuallySelected", False)),
        estimated_chars=int(item.get("estimatedChars", 0)),
    )


def _candidate(
    identifier: str,
    rank: int,
    coverage: tuple[str, ...],
    quality: float,
    *,
    manual: bool = False,
    chars: int = 0,
) -> CoverageEvidenceCandidate:
    return CoverageEvidenceCandidate(
        candidate_id=identifier,
        original_rank=rank,
        text=identifier,
        coverage=coverage,
        ranking_score=quality,
        topic_score=quality,
        entity_score=quality,
        technical_token_score=quality,
        source_trust=quality,
        version_score=quality,
        manually_selected=manual,
        estimated_chars=chars,
    )
