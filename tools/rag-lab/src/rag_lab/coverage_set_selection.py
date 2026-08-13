from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Iterable


@dataclass(frozen=True, slots=True)
class CoverageEvidenceCandidate:
    candidate_id: str
    original_rank: int
    source_type: str = ""
    document_id: str | None = None
    file_path: str | None = None
    section: str | None = None
    text: str = ""
    content_hash: str | None = None
    technical_tokens: tuple[str, ...] = ()
    coverage: tuple[str, ...] = ()
    ranking_score: float = 0.0
    topic_score: float = 0.0
    entity_score: float = 0.0
    technical_token_score: float = 0.0
    source_trust: float = 0.0
    version_score: float = 0.0
    conflict_penalty: float = 0.0
    explicitly_excluded: bool = False
    topic_conflict: bool = False
    product_mismatch: bool = False
    manually_selected: bool = False
    estimated_chars: int = 0


@dataclass(frozen=True, slots=True)
class CoverageEvidenceSelectionRequest:
    required_coverage: tuple[str, ...]
    candidates: tuple[CoverageEvidenceCandidate, ...]
    corpus_coverage: tuple[str, ...] | None = None
    base_max_items: int = 3
    expansion_max_items: int = 5
    character_budget: int = 6000
    minimum_quality_score: float = 0.30


@dataclass(frozen=True, slots=True)
class CoverageEvidenceSelectionResult:
    selected: tuple[CoverageEvidenceCandidate, ...]
    decisions: tuple[CoverageEvidenceSelectionDecision, ...]
    required_coverage: tuple[str, ...]
    search_coverage: tuple[str, ...]
    selected_coverage: tuple[str, ...]
    missing_coverage: tuple[str, ...]
    statuses: tuple[str, ...]
    warnings: tuple[str, ...]
    redundant_candidates_skipped: int
    budget_limited: bool
    estimated_chars: int
    selection_mode: str = "CoverageAware"


@dataclass(frozen=True, slots=True)
class _Assessment:
    candidate: CoverageEvidenceCandidate
    new_required_coverage_count: int
    quality_score: float
    set_score: float


@dataclass(frozen=True, slots=True)
class CoverageEvidenceSelectionDecision:
    candidate_id: str
    quality_score: float
    set_score: float
    added_coverage: tuple[str, ...]
    manually_selected: bool
    reason: str


def select_coverage_evidence(
    request: CoverageEvidenceSelectionRequest,
) -> CoverageEvidenceSelectionResult:
    required = _normalize(request.required_coverage)
    candidates = sorted(
        (item for item in request.candidates if item.candidate_id.strip()),
        key=lambda item: (item.original_rank, item.candidate_id),
    )
    base_max = min(5, max(1, request.base_max_items))
    expansion_max = min(5, max(base_max, request.expansion_max_items))
    budget = max(0, request.character_budget)
    minimum_quality = _clamp(request.minimum_quality_score)
    selected: list[CoverageEvidenceCandidate] = []
    decisions: list[CoverageEvidenceSelectionDecision] = []
    selected_ids: set[str] = set()
    selected_coverage: set[str] = set()
    warnings: list[str] = []
    used_chars = 0
    redundant_ids: set[str] = set()
    budget_limited = False

    for manual in (item for item in candidates if item.manually_selected):
        if manual.candidate_id in selected_ids:
            continue
        selected.append(manual)
        selected_ids.add(manual.candidate_id)
        added_coverage = tuple(
            item
            for item in _normalize(manual.coverage)
            if item in required and item not in selected_coverage
        )
        selected_coverage.update(_normalize(manual.coverage))
        used_chars += _estimated_chars(manual)
        decisions.append(
            CoverageEvidenceSelectionDecision(
                manual.candidate_id,
                _quality_score(manual),
                _quality_score(manual),
                added_coverage,
                True,
                "ManualSelection",
            )
        )

    if len(selected) > expansion_max:
        warnings.append("ManualSelectionExceedsLimit")
    if selected and used_chars > budget:
        warnings.append("ManualSelectionExceedsCharacterBudget")

    eligible = [
        item
        for item in candidates
        if item.candidate_id not in selected_ids
        and not item.explicitly_excluded
        and not item.topic_conflict
        and not item.product_mismatch
        and _quality_score(item) >= minimum_quality
    ]

    while eligible and len(selected) < expansion_max:
        coverage_complete = not required or all(
            item in selected_coverage for item in required
        )
        if len(selected) >= base_max and coverage_complete:
            break

        has_anchor = bool(selected)
        ranked = sorted(
            (
                _assess(item, selected, selected_coverage, required)
                for item in eligible
            ),
            key=lambda item: (
                -(item.new_required_coverage_count if has_anchor else 0),
                -item.set_score,
                -item.quality_score,
                item.candidate.original_rank,
                item.candidate.candidate_id,
            ),
        )
        chosen: _Assessment | None = None
        for assessment in ranked:
            if (
                len(selected) >= base_max
                and assessment.new_required_coverage_count == 0
            ):
                continue
            if _is_redundant(
                assessment.candidate,
                selected,
                assessment.new_required_coverage_count > 0,
            ):
                redundant_ids.add(assessment.candidate.candidate_id)
                continue
            candidate_chars = _estimated_chars(assessment.candidate)
            if selected and used_chars + candidate_chars > budget:
                if assessment.new_required_coverage_count > 0:
                    budget_limited = True
                continue
            chosen = assessment
            break

        if chosen is None:
            break
        selected.append(chosen.candidate)
        selected_ids.add(chosen.candidate.candidate_id)
        added_coverage = tuple(
            item
            for item in _normalize(chosen.candidate.coverage)
            if item in required and item not in selected_coverage
        )
        selected_coverage.update(_normalize(chosen.candidate.coverage))
        used_chars += _estimated_chars(chosen.candidate)
        eligible.remove(chosen.candidate)
        decisions.append(
            CoverageEvidenceSelectionDecision(
                chosen.candidate.candidate_id,
                chosen.quality_score,
                chosen.set_score,
                added_coverage,
                False,
                "QualityAnchor" if len(selected) == 1 else "CoverageCompletion",
            )
        )

    search_coverage = _normalize(
        coverage for item in candidates for coverage in item.coverage
    )
    missing = tuple(item for item in required if item not in selected_coverage)
    statuses = _statuses(
        request.corpus_coverage,
        required,
        search_coverage,
        missing,
        budget_limited,
        len(selected) >= expansion_max,
    )
    return CoverageEvidenceSelectionResult(
        selected=tuple(selected),
        decisions=tuple(decisions),
        required_coverage=required,
        search_coverage=search_coverage,
        selected_coverage=tuple(sorted(selected_coverage)),
        missing_coverage=missing,
        statuses=statuses,
        warnings=tuple(warnings),
        redundant_candidates_skipped=len(redundant_ids),
        budget_limited=budget_limited,
        estimated_chars=used_chars,
    )


def _assess(
    candidate: CoverageEvidenceCandidate,
    selected: list[CoverageEvidenceCandidate],
    selected_coverage: set[str],
    required: tuple[str, ...],
) -> _Assessment:
    candidate_coverage = _normalize(candidate.coverage)
    new_required = sum(
        item in required and item not in selected_coverage
        for item in candidate_coverage
    )
    quality = _quality_score(candidate)
    source_diversity = float(
        not selected
        or all(
            item.source_type.casefold() != candidate.source_type.casefold()
            for item in selected
        )
    )
    technical_diversity = (
        1.0
        if not selected
        else 1.0
        - max(
            _jaccard(item.technical_tokens, candidate.technical_tokens)
            for item in selected
        )
    )
    coverage_value = 0 if not selected else new_required
    set_score = (
        coverage_value
        + 0.35 * quality
        + 0.05 * source_diversity
        + 0.05 * technical_diversity
    )
    return _Assessment(candidate, new_required, quality, set_score)


def _quality_score(item: CoverageEvidenceCandidate) -> float:
    return _clamp(
        0.45 * _clamp(item.ranking_score)
        + 0.20 * _clamp(item.topic_score)
        + 0.10 * _clamp(item.entity_score)
        + 0.10 * _clamp(item.technical_token_score)
        + 0.10 * _clamp(item.source_trust)
        + 0.05 * _clamp(item.version_score)
        + item.conflict_penalty
    )


def _is_redundant(
    candidate: CoverageEvidenceCandidate,
    selected: list[CoverageEvidenceCandidate],
    adds_required_coverage: bool,
) -> bool:
    for existing in selected:
        if candidate.content_hash and _same(candidate.content_hash, existing.content_hash):
            return True
        same_document = _same(candidate.document_id, existing.document_id) or _same(
            candidate.file_path, existing.file_path
        )
        same_section = _same(candidate.section, existing.section)
        if same_document and (same_section or not adds_required_coverage):
            return True
        if not adds_required_coverage and _text_similarity(
            candidate.text, existing.text
        ) >= 0.88:
            return True
        distinct_technical_tokens = {
            token.strip().casefold()
            for token in (*candidate.technical_tokens, *existing.technical_tokens)
            if token.strip()
        }
        if (
            not adds_required_coverage
            and len(distinct_technical_tokens) >= 2
            and _jaccard(candidate.technical_tokens, existing.technical_tokens) >= 0.90
        ):
            return True
    return False


def _statuses(
    corpus_coverage: tuple[str, ...] | None,
    required: tuple[str, ...],
    search_coverage: tuple[str, ...],
    missing: tuple[str, ...],
    budget_limited: bool,
    item_limit_reached: bool,
) -> tuple[str, ...]:
    statuses: list[str] = []
    if corpus_coverage is not None and set(required) - set(_normalize(corpus_coverage)):
        statuses.append("MissingCoverageInCorpus")
    if set(required) - set(search_coverage):
        statuses.append("MissingCoverageInSearchResults")
    if not missing:
        statuses.append("CoverageSatisfied")
    elif (budget_limited or item_limit_reached) and any(
        item in search_coverage for item in missing
    ):
        statuses.append("SelectionBudgetExceeded")
    else:
        statuses.append("CoverageInsufficient")
    return tuple(statuses)


def _normalize(values: Iterable[str]) -> tuple[str, ...]:
    return tuple(sorted({value.strip() for value in values if value.strip()}))


def _estimated_chars(item: CoverageEvidenceCandidate) -> int:
    return item.estimated_chars if item.estimated_chars > 0 else len(item.text)


def _same(left: str | None, right: str | None) -> bool:
    return bool(left and right and left.strip().casefold() == right.strip().casefold())


def _text_similarity(left: str, right: str) -> float:
    return _jaccard(_terms(left), _terms(right))


def _terms(value: str) -> tuple[str, ...]:
    return tuple(
        item.upper()
        for item in re.split(r"[\s,.:;/\\_-]+", value)
        if len(item) > 1
    )[:400]


def _jaccard(left: Iterable[str], right: Iterable[str]) -> float:
    left_set = {item.casefold() for item in left if item.strip()}
    right_set = {item.casefold() for item in right if item.strip()}
    if not left_set or not right_set:
        return 0.0
    return len(left_set & right_set) / len(left_set | right_set)


def _clamp(value: float) -> float:
    return min(1.0, max(0.0, value))
