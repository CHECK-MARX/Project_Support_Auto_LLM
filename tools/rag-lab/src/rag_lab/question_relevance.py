from __future__ import annotations

import hashlib
import re
import unicodedata
from dataclasses import dataclass
from enum import StrEnum
from time import perf_counter
from typing import Iterable

from .search import SearchResult


class QuestionType(StrEnum):
    COMMAND = "CommandQuestion"
    HOW_TO = "HowToQuestion"
    CONFIGURATION = "ConfigurationQuestion"
    TROUBLESHOOTING = "TroubleshootingQuestion"
    VERSION = "VersionQuestion"
    PERMISSION = "PermissionQuestion"
    ERROR_MESSAGE = "ErrorMessageQuestion"


class CoverageItem(StrEnum):
    UPLOAD_COMMAND = "A:UploadCommand"
    COMMAND_OPTIONS = "B:CommandOptions"
    AUTHENTICATION = "C:Authentication"
    PROJECT_ASSOCIATION = "D:ProjectAssociation"
    EXECUTION = "E:Execution"
    VALIDATE_VERIFICATION = "F:ValidateVerification"
    FAILURE_CHECKS = "G:FailureChecks"


class InsufficientReason(StrEnum):
    NO_RELEVANT_EVIDENCE = "NoRelevantEvidence"
    MISSING_COMMAND = "MissingCommand"
    MISSING_PROCEDURE = "MissingProcedure"
    MISSING_VERSION = "MissingVersionSpecificEvidence"
    PRODUCT_MISMATCH = "ProductMismatch"
    CONFLICTING_EVIDENCE = "ConflictingEvidence"
    LOW_COVERAGE = "LowCoverage"
    LOW_CONFIDENCE = "LowConfidence"


@dataclass(frozen=True, slots=True)
class QuestionProfile:
    types: tuple[QuestionType, ...]
    technical_tokens: tuple[str, ...]
    requested_version: str | None
    upload_workflow: bool


@dataclass(frozen=True, slots=True)
class EvidenceAssessment:
    result: SearchResult
    score: float
    question_type_score: float
    technical_token_score: float
    source_trust_score: float
    coverage: frozenset[CoverageItem]
    product_match: bool | None
    version_match: str
    exact_technical_tokens: tuple[str, ...]
    text_fingerprint: str


@dataclass(frozen=True, slots=True)
class QuestionAwareSelection:
    selected: tuple[EvidenceAssessment, ...]
    profile: QuestionProfile
    final_coverage: frozenset[CoverageItem]
    insufficient_reasons: tuple[InsufficientReason, ...]
    elapsed_ms: float


_OPTION = re.compile(r"--[A-Za-z0-9][A-Za-z0-9_-]*")
_IDENTIFIER = re.compile(
    r"(?<![A-Za-z0-9_])[A-Za-z][A-Za-z0-9_.+#/-]{2,}(?![A-Za-z0-9_])"
)
_VERSION = re.compile(
    r"(?<![A-Za-z0-9_.])v?(\d+(?:\.\d+){1,3})(?![A-Za-z0-9_.])",
    re.IGNORECASE,
)
_CODE_BLOCK = re.compile(r"```|(?:^|\n)\s*(?:\$|>|C:\\|qacli\b)", re.IGNORECASE)
_NUMBERED_STEP = re.compile(r"(?:^|\n)\s*(?:\d+[.)]|step\s+\d+|手順\s*\d+)", re.IGNORECASE)


def _normalize(value: str | None) -> str:
    return unicodedata.normalize("NFKC", value or "").strip().casefold()


def _contains(text: str, *terms: str) -> bool:
    return any(_normalize(term) in text for term in terms)


def classify_question(query: str) -> QuestionProfile:
    normalized = _normalize(query)
    types: list[QuestionType] = []
    if _contains(normalized, "コマンド", "command", "cli", "qacli") or _OPTION.search(query):
        types.append(QuestionType.COMMAND)
    if _contains(normalized, "手順", "方法", "how to", "howto", "一連", "完了まで", "使い方"):
        types.append(QuestionType.HOW_TO)
    if _contains(normalized, "設定", "configuration", "configure", "config"):
        types.append(QuestionType.CONFIGURATION)
    if _contains(normalized, "原因", "対処", "解決", "失敗", "動かない", "troubleshoot", "exception", "timeout"):
        types.append(QuestionType.TROUBLESHOOTING)
    if _contains(normalized, "バージョン", "version") or _VERSION.search(query):
        types.append(QuestionType.VERSION)
    if _contains(normalized, "権限", "permission", "access denied", "unauthorized", "forbidden"):
        types.append(QuestionType.PERMISSION)
    if _contains(normalized, "エラーメッセージ", "エラーコード", "error message", "error code"):
        types.append(QuestionType.ERROR_MESSAGE)

    technical = extract_exact_technical_tokens(query)
    version_match = _VERSION.search(query)
    requested_version = version_match.group(1) if version_match else None
    upload_workflow = _contains(normalized, "upload", "アップロード") and _contains(normalized, "validate")
    return QuestionProfile(tuple(dict.fromkeys(types)), technical, requested_version, upload_workflow)


def extract_exact_technical_tokens(query: str) -> tuple[str, ...]:
    """Return only explicit query-side technical tokens; never add synonyms."""
    values = [*_OPTION.findall(query), *_IDENTIFIER.findall(query)]
    ignored = {
        "about", "please", "具体的に", "について", "ください", "教えてください",
    }
    unique: dict[str, str] = {}
    for value in values:
        normalized = _normalize(value).strip(".,:;()[]{}")
        if len(normalized) < 3 or normalized in ignored:
            continue
        unique.setdefault(normalized, value.strip(".,:;()[]{}"))
    return tuple(unique[key] for key in sorted(unique))


def _document_text(result: SearchResult) -> str:
    metadata = result.chunk.metadata
    return "\n".join(
        value
        for value in (
            result.chunk.section_title,
            metadata.document_name,
            metadata.command,
            metadata.question,
            metadata.resolution,
            result.chunk.text,
        )
        if value
    )


def _coverage(text: str, profile: QuestionProfile) -> frozenset[CoverageItem]:
    if not profile.upload_workflow:
        return frozenset()
    normalized = _normalize(text)
    values: set[CoverageItem] = set()
    has_command = _contains(normalized, "qacli", "command", "コマンド") or _CODE_BLOCK.search(text)
    if has_command and _contains(normalized, "upload", "アップロード", "validate"):
        values.add(CoverageItem.UPLOAD_COMMAND)
    if _OPTION.search(text) or _contains(normalized, "option", "オプション", "parameter", "引数"):
        values.add(CoverageItem.COMMAND_OPTIONS)
    if _contains(normalized, "auth", "authentication", "login", "credential", "token", "認証", "ログイン"):
        values.add(CoverageItem.AUTHENTICATION)
    if _contains(normalized, "project", "プロジェクト", "associate", "関連付け"):
        values.add(CoverageItem.PROJECT_ASSOCIATION)
    if has_command or _contains(normalized, "execute", "run", "実行"):
        values.add(CoverageItem.EXECUTION)
    if _contains(normalized, "validate") and _contains(normalized, "confirm", "verify", "portal", "確認", "表示"):
        values.add(CoverageItem.VALIDATE_VERIFICATION)
    if _contains(normalized, "fail", "error", "check", "失敗", "エラー", "確認事項", "troubleshoot"):
        values.add(CoverageItem.FAILURE_CHECKS)
    return frozenset(values)


def _question_type_score(text: str, profile: QuestionProfile) -> float:
    normalized = _normalize(text)
    scores: list[float] = []
    if QuestionType.COMMAND in profile.types:
        features = (
            bool(_CODE_BLOCK.search(text)), bool(_OPTION.search(text)),
            _contains(normalized, "qacli", "cli", "command", "コマンド"),
            _contains(normalized, "parameter", "option", "引数", "オプション"),
        )
        scores.append(sum(features) / len(features))
    if QuestionType.HOW_TO in profile.types:
        features = (
            bool(_NUMBERED_STEP.search(text)),
            _contains(normalized, "prerequisite", "事前", "前提"),
            _contains(normalized, "auth", "login", "認証", "接続"),
            _contains(normalized, "execute", "run", "実行"),
            _contains(normalized, "success", "complete", "完了", "成功"),
            _contains(normalized, "verify", "confirm", "確認"),
        )
        scores.append(sum(features) / len(features))
    mappings = {
        QuestionType.CONFIGURATION: ("設定", "config", "option"),
        QuestionType.TROUBLESHOOTING: ("原因", "対処", "error", "失敗"),
        QuestionType.VERSION: ("version", "バージョン", "release"),
        QuestionType.PERMISSION: ("permission", "権限", "role", "access"),
        QuestionType.ERROR_MESSAGE: ("error message", "error code", "エラーメッセージ", "エラーコード"),
    }
    for question_type, terms in mappings.items():
        if question_type in profile.types:
            scores.append(1.0 if _contains(normalized, *terms) else 0.0)
    return max(scores, default=0.0)


def _source_trust(source_type: str | None) -> float:
    normalized = _normalize(source_type).replace("_", "").replace("-", "")
    if "officialdoc" in normalized:
        return 1.0
    if "manual" in normalized:
        return 0.88
    if "manufacturerreply" in normalized:
        return 0.78
    if "verifiedpastanswer" in normalized or "exactpastanswer" in normalized:
        return 0.70
    if "pastcase" in normalized or "pastanswer" in normalized:
        return 0.56
    if "internalnote" in normalized:
        return 0.35
    return 0.45


def _version_status(requested: str | None, actual: str | None) -> str:
    if requested is None:
        return "not_requested"
    if not actual:
        return "unknown"
    left = [int(item) for item in requested.split(".")]
    matches = _VERSION.search(actual)
    if not matches:
        return "unknown"
    right = [int(item) for item in matches.group(1).split(".")]
    if left == right:
        return "exact"
    if left[:2] == right[:2]:
        return "near"
    return "mismatch"


def assess_evidence(
    query: str,
    result: SearchResult,
    *,
    product: str | None = None,
) -> EvidenceAssessment:
    profile = classify_question(query)
    text = _document_text(result)
    normalized_text = _normalize(text)
    exact = tuple(token for token in profile.technical_tokens if _normalize(token) in normalized_text)
    technical_score = len(exact) / len(profile.technical_tokens) if profile.technical_tokens else 0.0
    coverage = _coverage(text, profile)
    coverage_score = len(coverage) / len(CoverageItem) if profile.upload_workflow else 0.0
    question_score = _question_type_score(text, profile)
    trust = _source_trust(result.chunk.metadata.source_type)
    expected_product = _normalize(product)
    actual_product = _normalize(result.chunk.metadata.product_name)
    product_match = (
        actual_product == expected_product
        if expected_product and actual_product
        else None
    )
    version_match = _version_status(profile.requested_version, result.chunk.metadata.target_version)
    base_score = max(0.0, min(1.0, result.score))
    score = 0.43 * base_score + 0.24 * question_score + 0.16 * technical_score + 0.11 * coverage_score + 0.06 * trust
    if product_match is False:
        score -= 0.45
    if version_match == "exact":
        score += 0.04
    elif version_match == "mismatch":
        score -= 0.12
    fingerprint = hashlib.sha256(_normalize(result.chunk.text).encode("utf-8")).hexdigest()
    return EvidenceAssessment(
        result=result,
        score=max(0.0, min(1.0, score)),
        question_type_score=question_score,
        technical_token_score=technical_score,
        source_trust_score=trust,
        coverage=coverage,
        product_match=product_match,
        version_match=version_match,
        exact_technical_tokens=exact,
        text_fingerprint=fingerprint,
    )


def select_question_aware_evidence(
    query: str,
    results: Iterable[SearchResult],
    *,
    product: str | None = None,
    top_k: int = 3,
) -> QuestionAwareSelection:
    if not 1 <= top_k <= 5:
        raise ValueError("question-aware top_k must be between 1 and 5")
    started = perf_counter()
    profile = classify_question(query)
    materialized = list(results)
    all_assessed = [assess_evidence(query, result, product=product) for result in materialized]
    assessed = [item for item in all_assessed if item.product_match is not False]
    selected: list[EvidenceAssessment] = []
    seen_fingerprints: set[str] = set()
    covered: set[CoverageItem] = set()
    while assessed and len(selected) < top_k:
        candidates = [item for item in assessed if item.text_fingerprint not in seen_fingerprints]
        if not candidates:
            break
        best = max(
            candidates,
            key=lambda item: (
                item.score + 0.12 * len(item.coverage - covered),
                item.question_type_score,
                item.technical_token_score,
                -item.result.rank,
                item.result.chunk.chunk_id,
            ),
        )
        selected.append(best)
        assessed.remove(best)
        seen_fingerprints.add(best.text_fingerprint)
        covered.update(best.coverage)

    reasons: list[InsufficientReason] = []
    if not selected:
        reasons.append(InsufficientReason.NO_RELEVANT_EVIDENCE)
        if all_assessed and all(item.product_match is False for item in all_assessed):
            reasons.append(InsufficientReason.PRODUCT_MISMATCH)
    if QuestionType.COMMAND in profile.types and profile.upload_workflow and CoverageItem.UPLOAD_COMMAND not in covered:
        reasons.append(InsufficientReason.MISSING_COMMAND)
    if QuestionType.HOW_TO in profile.types and profile.upload_workflow:
        procedure = {CoverageItem.EXECUTION, CoverageItem.VALIDATE_VERIFICATION}
        if not procedure.issubset(covered):
            reasons.append(InsufficientReason.MISSING_PROCEDURE)
    if profile.requested_version and not any(item.version_match in {"exact", "near"} for item in selected):
        reasons.append(InsufficientReason.MISSING_VERSION)
    versions = {
        item.result.chunk.metadata.target_version
        for item in selected
        if item.result.chunk.metadata.target_version
    }
    if len(versions) > 1:
        reasons.append(InsufficientReason.CONFLICTING_EVIDENCE)
    if profile.upload_workflow and len(covered) < 4:
        reasons.append(InsufficientReason.LOW_COVERAGE)
    if selected and max(item.score for item in selected) < 0.25:
        reasons.append(InsufficientReason.LOW_CONFIDENCE)
    return QuestionAwareSelection(
        selected=tuple(selected),
        profile=profile,
        final_coverage=frozenset(covered),
        insufficient_reasons=tuple(dict.fromkeys(reasons)),
        elapsed_ms=(perf_counter() - started) * 1000.0,
    )


def comparison_report(
    query: str,
    legacy: Iterable[SearchResult],
    question_aware: QuestionAwareSelection,
    *,
    top_k: int,
) -> dict[str, object]:
    legacy_list = list(legacy)[:top_k]
    return {
        "dataClassification": "synthetic",
        "questionTypes": [item.value for item in question_aware.profile.types],
        "phase14": {
            "topIds": [item.chunk.chunk_id for item in legacy_list],
            "sources": [item.chunk.metadata.source_type or "Unknown" for item in legacy_list],
            "scores": [round(item.score, 8) for item in legacy_list],
        },
        "phase15": {
            "topIds": [item.result.chunk.chunk_id for item in question_aware.selected],
            "items": [
                {
                    "source": item.result.chunk.metadata.source_type or "Unknown",
                    "score": round(item.score, 8),
                    "questionTypeScore": round(item.question_type_score, 8),
                    "technicalTokenScore": round(item.technical_token_score, 8),
                    "coverage": sorted(value.value for value in item.coverage),
                    "versionMatch": item.version_match,
                    "productMatch": item.product_match,
                    "selectionReason": "question relevance, exact tokens, coverage diversity, product/version and source trust",
                }
                for item in question_aware.selected
            ],
            "finalCoverage": sorted(item.value for item in question_aware.final_coverage),
            "insufficientReasons": [item.value for item in question_aware.insufficient_reasons],
            "searchTimeMs": round(question_aware.elapsed_ms, 3),
        },
    }


def select_evidence_by_mode(
    ranking_mode: str,
    query: str,
    results: Iterable[SearchResult],
    *,
    product: str | None = None,
    top_k: int = 3,
):
    """Keep Phase 15 exact unless Phase 16 is explicitly requested."""
    materialized = list(results)
    if ranking_mode.casefold() != "phase16":
        return select_question_aware_evidence(
            query, materialized, product=product, top_k=top_k
        )

    from .topic_entity_ranking import rank_topic_entity_candidates

    return rank_topic_entity_candidates(
        build_topic_entity_ranking_request(
            query,
            materialized,
            product=product,
            top_k=top_k,
        )
    )


def build_topic_entity_ranking_request(
    query: str,
    results: Iterable[SearchResult],
    *,
    product: str | None = None,
    requested_version: str | None = None,
    top_k: int = 3,
):
    """Adapt search output to the pure Phase 16 ranking input."""
    from .topic_entity_ranking import (
        AliasDefinition,
        EntityAliasDefinition,
        EntityKind,
        TopicEntityCatalog,
        TopicEntityRankingCandidate,
        TopicEntityRankingRequest,
        extract_topic_entity_profile,
    )

    catalog = TopicEntityCatalog(
        products=(AliasDefinition(product),) if product else (),
        components=(AliasDefinition("Validate", ("Perforce Validate",)),),
        features=(
            AliasDefinition("Stream", ("ストリーム",)),
            AliasDefinition("License", ("ライセンス",)),
            AliasDefinition("IDE Plugin", ("IDEプラグイン", "Eclipse Plugin")),
            AliasDefinition("Build upload", ("validate build", "build upload")),
        ),
        objects=(AliasDefinition("Analysis result", ("解析結果",)),),
        entities=(
            EntityAliasDefinition(
                EntityKind.COMMAND,
                "qacli validate build",
                ("validate build",),
            ),
        ),
    )
    candidates = tuple(
        TopicEntityRankingCandidate(
            candidate_index=index,
            candidate_id=result.chunk.chunk_id,
            text=(text := _document_text(result)),
            source_type=result.chunk.metadata.source_type or "",
            product_name=result.chunk.metadata.product_name,
            version=result.chunk.metadata.target_version,
            base_search_score=result.score,
            original_rank=result.rank,
            profile=extract_topic_entity_profile(text, catalog),
        )
        for index, result in enumerate(results)
    )
    return TopicEntityRankingRequest(
        query_profile=extract_topic_entity_profile(query, catalog),
        technical_tokens=extract_exact_technical_tokens(query),
        requested_product=product,
        requested_version=requested_version,
        candidates=candidates,
        max_items=top_k,
    )
