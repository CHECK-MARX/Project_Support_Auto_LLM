from __future__ import annotations

import hashlib
import re
import unicodedata
from dataclasses import dataclass
from enum import StrEnum
from typing import Iterable


class EntityKind(StrEnum):
    PRODUCT = "Product"
    FEATURE = "Feature"
    COMMAND = "Command"
    OPTION = "Option"
    API = "Api"
    SETTING = "Setting"
    ERROR_CODE = "ErrorCode"
    FILE = "File"
    VERSION = "Version"
    OPERATING_SYSTEM = "OperatingSystem"
    SERVER_TYPE = "ServerType"


@dataclass(frozen=True, slots=True)
class AliasDefinition:
    canonical_name: str
    aliases: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class EntityAliasDefinition:
    kind: EntityKind
    canonical_value: str
    aliases: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class TopicEntityCatalog:
    products: tuple[AliasDefinition, ...] = ()
    components: tuple[AliasDefinition, ...] = ()
    features: tuple[AliasDefinition, ...] = ()
    objects: tuple[AliasDefinition, ...] = ()
    entities: tuple[EntityAliasDefinition, ...] = ()


@dataclass(frozen=True, slots=True)
class TopicEntity:
    kind: EntityKind
    value: str
    normalized_value: str


@dataclass(frozen=True, slots=True)
class TopicEntityProfile:
    products: tuple[str, ...] = ()
    components: tuple[str, ...] = ()
    features: tuple[str, ...] = ()
    operations: tuple[str, ...] = ()
    objects: tuple[str, ...] = ()
    intents: tuple[str, ...] = ()
    entities: tuple[TopicEntity, ...] = ()


@dataclass(frozen=True, slots=True)
class TopicConflictAssessment:
    topic_conflict: bool
    has_topic_match: bool
    no_topic_match: bool
    conflict_kinds: tuple[str, ...]
    matched_products: tuple[str, ...]
    matched_components: tuple[str, ...]
    matched_features: tuple[str, ...]
    matched_entities: tuple[TopicEntity, ...]


class TopicCoverage(StrEnum):
    OVERVIEW = "A:FeatureOverview"
    PURPOSE = "B:FeaturePurpose"
    SETUP = "C:SetupOrCreation"
    ASSOCIATION = "D:ProductAssociation"
    VERIFICATION = "E:Verification"
    UPLOAD_COMMAND = "U-A:UploadCommand"
    COMMAND_OPTIONS = "U-B:CommandOptions"
    AUTHENTICATION = "U-C:Authentication"
    PROJECT_ASSOCIATION = "U-D:ProjectAssociation"
    EXECUTION = "U-E:Execution"
    VALIDATE_VERIFICATION = "U-F:ValidateVerification"
    FAILURE_CHECKS = "U-G:FailureChecks"


@dataclass(frozen=True, slots=True)
class TopicEntityRankingCandidate:
    candidate_index: int
    candidate_id: str
    text: str
    source_type: str
    product_name: str | None
    version: str | None
    base_search_score: float
    original_rank: int
    profile: TopicEntityProfile
    lexical_score: float | None = None
    semantic_score: float | None = None
    manually_selected: bool = False


@dataclass(frozen=True, slots=True)
class TopicEntityRankingRequest:
    query_profile: TopicEntityProfile
    candidates: tuple[TopicEntityRankingCandidate, ...]
    technical_tokens: tuple[str, ...] = ()
    requested_product: str | None = None
    requested_version: str | None = None
    max_items: int = 3
    excluded_profile: TopicEntityProfile = TopicEntityProfile()


@dataclass(frozen=True, slots=True)
class TopicEntityRankingAssessment:
    candidate_index: int
    candidate_id: str
    final_score: float
    topic_score: float
    product_score: float
    component_score: float
    feature_score: float
    operation_score: float
    intent_score: float
    entity_score: float
    technical_token_score: float
    base_search_score: float
    lexical_score: float | None
    semantic_score: float | None
    source_trust_score: float
    version_score: float
    conflict_penalty: float
    exclusion_penalty: float
    explicitly_excluded: bool
    product_match: bool | None
    version_match: str
    topic_conflict: bool
    has_topic_match: bool
    conflict_kinds: tuple[str, ...]
    coverage: frozenset[TopicCoverage]
    exact_technical_tokens: tuple[str, ...]
    text_fingerprint: str
    selection_reason: str


@dataclass(frozen=True, slots=True)
class TopicEntityRankingResult:
    selected: tuple[TopicEntityRankingAssessment, ...]
    assessed: tuple[TopicEntityRankingAssessment, ...]
    final_coverage: frozenset[TopicCoverage]
    insufficient_reasons: tuple[str, ...]


def build_topic_entity_comparison_section(
    request: TopicEntityRankingRequest,
    result: TopicEntityRankingResult,
    *,
    elapsed_ms: float,
) -> tuple[dict[str, object], dict[str, object]]:
    """Build a redacted Phase 16 diagnostic section and synthetic quality check."""
    candidate_by_index = {
        candidate.candidate_index: candidate for candidate in request.candidates
    }

    def profile_payload(profile: TopicEntityProfile) -> dict[str, object]:
        return {
            "products": list(profile.products),
            "components": list(profile.components),
            "features": list(profile.features),
            "operations": list(profile.operations),
            "objects": list(profile.objects),
            "intents": list(profile.intents),
            "entities": [
                {"kind": item.kind.value, "value": item.value}
                for item in profile.entities
            ],
        }

    items: list[dict[str, object]] = []
    for assessment in result.selected:
        candidate = candidate_by_index[assessment.candidate_index]
        items.append(
            {
                "id": assessment.candidate_id,
                "topic": profile_payload(candidate.profile),
                "sourceType": candidate.source_type or "Unknown",
                "score": round(assessment.final_score, 8),
                "topicScore": round(assessment.topic_score, 8),
                "entityScore": round(assessment.entity_score, 8),
                "conflictPenalty": round(assessment.conflict_penalty, 8),
                "coverage": sorted(item.value for item in assessment.coverage),
                "productMatch": assessment.product_match,
                "versionMatch": assessment.version_match,
                "selectionReason": assessment.selection_reason,
            }
        )

    selected_rank = {
        item.candidate_id: index
        for index, item in enumerate(result.selected, start=1)
    }
    stream_assessed = [
        item
        for item in result.assessed
        if "Stream" in candidate_by_index[item.candidate_index].profile.features
    ]
    license_assessed = [
        item
        for item in result.assessed
        if "License" in candidate_by_index[item.candidate_index].profile.features
    ]
    applicable = "Stream" in request.query_profile.features and bool(stream_assessed)
    stream_rank = min(
        (selected_rank[item.candidate_id] for item in stream_assessed if item.candidate_id in selected_rank),
        default=None,
    )
    license_rank = min(
        (selected_rank[item.candidate_id] for item in license_assessed if item.candidate_id in selected_rank),
        default=None,
    )
    condition_passed = bool(
        applicable
        and stream_rank is not None
        and (license_rank is None or stream_rank < license_rank)
    )
    quality = {
        "name": "StreamEvidenceRanksAboveLicenseEvidence",
        "applicable": applicable,
        "passed": condition_passed if applicable else None,
        "streamTopRank": stream_rank,
        "licenseTopRank": license_rank,
        "streamCandidateScores": {
            item.candidate_id: round(item.final_score, 8) for item in stream_assessed
        },
        "licenseCandidateScores": {
            item.candidate_id: round(item.final_score, 8) for item in license_assessed
        },
    }
    section = {
        "queryTopic": profile_payload(request.query_profile),
        "topIds": [item.candidate_id for item in result.selected],
        "items": items,
        "finalCoverage": sorted(item.value for item in result.final_coverage),
        "insufficientReasons": list(result.insufficient_reasons),
        "rankingTimeMs": round(elapsed_ms, 3),
    }
    return section, quality


_OPERATION_TERMS: tuple[tuple[str, tuple[str, ...]], ...] = (
    ("Configuration", ("設定", "構成", "configure", "configuration", "setup", "set up")),
    ("Creation", ("作成", "生成", "create", "creation", "generate")),
    ("Upload", ("アップロード", "upload")),
    ("Verification", ("確認", "検証", "verify", "verification", "check")),
    ("Association", ("関連付け", "紐付け", "associate", "association", "link")),
    ("Execution", ("実行", "起動", "run", "execute", "launch")),
    ("Troubleshooting", ("原因", "対処", "解決", "troubleshoot", "failure", "failed")),
)

_INTENT_TERMS: tuple[tuple[str, tuple[str, ...]], ...] = (
    ("Overview", ("概要", "何ですか", "とは", "what is", "overview")),
    ("Purpose", ("用途", "目的", "何に使", "purpose", "use case")),
    ("HowTo", ("方法", "手順", "やり方", "how to", "procedure")),
    ("Command", ("コマンド", "command", "cli", "qacli")),
    ("Configuration", ("設定", "構成", "configuration", "configure", "setup")),
    ("Verification", ("確認", "検証", "verify", "verification")),
    ("Troubleshooting", ("原因", "対処", "解決", "troubleshoot", "error", "failed")),
)

_OPERATING_SYSTEMS: tuple[tuple[str, tuple[str, ...]], ...] = (
    ("Windows", ("windows",)),
    ("Linux", ("linux",)),
    ("macOS", ("macos", "mac os")),
    ("RHEL", ("rhel", "red hat enterprise linux")),
    ("Ubuntu", ("ubuntu",)),
)

_SERVER_TYPES: tuple[tuple[str, tuple[str, ...]], ...] = (
    ("License Server", ("license server", "ライセンスサーバー", "ライセンスサーバ")),
    ("Application Server", ("application server", "アプリケーションサーバー", "アプリケーションサーバ")),
    ("Web Server", ("web server", "webサーバー", "webサーバ")),
    ("Database Server", ("database server", "db server", "データベースサーバー", "データベースサーバ")),
)

_COMMAND = re.compile(
    r"(?<![A-Za-z0-9_])qacli(?:\s+[A-Za-z0-9_.+/-]+){1,3}", re.IGNORECASE
)
_OPTION = re.compile(r"(?<![A-Za-z0-9_])--[A-Za-z0-9][A-Za-z0-9_-]*")
_API = re.compile(
    r"(?<![A-Za-z0-9_])[A-Za-z][A-Za-z0-9_.]*(?:\s+)?(?:API|Api)(?![A-Za-z0-9_])"
)
_SETTING = re.compile(
    r"(?<![A-Za-z0-9_])(?:[A-Z][A-Z0-9]*_[A-Z0-9_]+(?:\s*=\s*[^\s,;]+)?|[A-Z][A-Z0-9_]{2,}\s*=\s*[^\s,;]+)"
)
_ERROR_CODE = re.compile(r"(?<![A-Za-z0-9_])[A-Z]{2,10}[-_]\d{2,8}(?![A-Za-z0-9_])")
_FILE = re.compile(
    r"(?<![A-Za-z0-9_.-])[A-Za-z0-9_.-]+\.(?:json|xml|yaml|yml|ini|conf|cfg|log|txt|csv|pdf|zip)(?![A-Za-z0-9_.-])",
    re.IGNORECASE,
)
_VERSION = re.compile(
    r"(?<![A-Za-z0-9_.])v?\d+(?:\.\d+){1,3}(?![A-Za-z0-9_.])", re.IGNORECASE
)


def normalize_text(value: str | None) -> str:
    normalized = unicodedata.normalize("NFKC", value or "").casefold()
    return " ".join(normalized.split())


def normalize_entity_value(kind: EntityKind, value: str | None) -> str:
    normalized = normalize_text(value)
    if kind is EntityKind.COMMAND and normalized.startswith("qacli "):
        return normalized[len("qacli ") :].strip()
    return normalized


def extract_topic_entity_profile(
    text: str | None, catalog: TopicEntityCatalog | None = None
) -> TopicEntityProfile:
    if not text or not text.strip():
        return TopicEntityProfile()

    catalog = catalog or TopicEntityCatalog()
    normalized = normalize_text(text)
    products = _match_aliases(normalized, catalog.products)
    components = _match_aliases(normalized, catalog.components)
    features = _match_aliases(normalized, catalog.features)
    objects = _match_aliases(normalized, catalog.objects)
    operations = _match_named_terms(normalized, _OPERATION_TERMS)
    intents = _match_named_terms(normalized, _INTENT_TERMS)
    entities: list[TopicEntity] = []

    for definition in catalog.entities:
        aliases = (definition.canonical_value, *definition.aliases)
        if any(_contains_alias(normalized, normalize_text(alias)) for alias in aliases):
            _add_entity(entities, definition.kind, definition.canonical_value)

    for kind, pattern in (
        (EntityKind.COMMAND, _COMMAND),
        (EntityKind.OPTION, _OPTION),
        (EntityKind.API, _API),
        (EntityKind.SETTING, _SETTING),
        (EntityKind.ERROR_CODE, _ERROR_CODE),
        (EntityKind.FILE, _FILE),
        (EntityKind.VERSION, _VERSION),
    ):
        for match in pattern.finditer(text):
            _add_entity(entities, kind, match.group(0))

    _add_known_entities(entities, normalized, EntityKind.OPERATING_SYSTEM, _OPERATING_SYSTEMS)
    _add_known_entities(entities, normalized, EntityKind.SERVER_TYPE, _SERVER_TYPES)

    for product in products:
        _add_entity(entities, EntityKind.PRODUCT, product)
    for feature in features:
        _add_entity(entities, EntityKind.FEATURE, feature)

    return TopicEntityProfile(
        products=products,
        components=components,
        features=features,
        operations=operations,
        objects=objects,
        intents=intents,
        entities=tuple(entities),
    )


def compare_topic_profiles(
    query: TopicEntityProfile, evidence: TopicEntityProfile
) -> TopicConflictAssessment:
    products = _intersect_values(query.products, evidence.products)
    components = _intersect_values(query.components, evidence.components)
    features = _intersect_values(query.features, evidence.features)
    entities = _intersect_entities(query.entities, evidence.entities)
    conflicts: list[str] = []

    _add_conflict(conflicts, "Product", query.products, evidence.products, products)
    _add_conflict(conflicts, "Component", query.components, evidence.components, components)
    _add_conflict(conflicts, "Feature", query.features, evidence.features, features)

    query_has_specific_topic = bool(query.features or query.components)
    has_topic_match = bool(
        features
        or (not query.features and components)
        or (not query.features and not query.components and products)
    )
    return TopicConflictAssessment(
        topic_conflict=bool(conflicts),
        has_topic_match=has_topic_match,
        no_topic_match=query_has_specific_topic and not has_topic_match,
        conflict_kinds=tuple(conflicts),
        matched_products=products,
        matched_components=components,
        matched_features=features,
        matched_entities=entities,
    )


def rank_topic_entity_candidates(
    request: TopicEntityRankingRequest,
) -> TopicEntityRankingResult:
    candidate_by_index = {
        candidate.candidate_index: candidate for candidate in request.candidates
    }
    assessed = tuple(_assess_ranking_candidate(request, candidate) for candidate in request.candidates)
    eligible = [
        item
        for item in assessed
        if candidate_by_index[item.candidate_index].manually_selected
        or (item.product_match is not False and not item.topic_conflict)
    ]
    selected: list[TopicEntityRankingAssessment] = []
    fingerprints: set[str] = set()
    covered: set[TopicCoverage] = set()
    limit = max(0, min(request.max_items, 5))
    while eligible and len(selected) < limit:
        candidates = [item for item in eligible if item.text_fingerprint not in fingerprints]
        if not candidates:
            break
        best = max(
            candidates,
            key=lambda item: (
                item.final_score + 0.06 * len(item.coverage - covered),
                item.topic_score,
                item.entity_score,
                item.base_search_score,
                -candidate_by_index[item.candidate_index].original_rank,
                item.candidate_id,
            ),
        )
        selected.append(best)
        eligible.remove(best)
        fingerprints.add(best.text_fingerprint)
        covered.update(best.coverage)

    reasons = _build_insufficient_reasons(
        request, assessed, tuple(selected), frozenset(covered), candidate_by_index
    )
    return TopicEntityRankingResult(
        selected=tuple(selected),
        assessed=assessed,
        final_coverage=frozenset(covered),
        insufficient_reasons=reasons,
    )


def _assess_ranking_candidate(
    request: TopicEntityRankingRequest,
    candidate: TopicEntityRankingCandidate,
) -> TopicEntityRankingAssessment:
    comparison = compare_topic_profiles(request.query_profile, candidate.profile)
    product_match = _product_match(request.requested_product, candidate.product_name)
    product_score = 1.0 if product_match is True else 0.0
    component_score = _match_score(request.query_profile.components, candidate.profile.components)
    feature_score = _match_score(request.query_profile.features, candidate.profile.features)
    operation_score = _match_score(request.query_profile.operations, candidate.profile.operations)
    intent_score = _match_score(request.query_profile.intents, candidate.profile.intents)
    query_entities = tuple(
        item
        for item in request.query_profile.entities
        if item.kind not in {EntityKind.PRODUCT, EntityKind.FEATURE}
    )
    entity_score = _matched_entity_score(query_entities, comparison.matched_entities)
    exact_tokens = tuple(
        token
        for token in request.technical_tokens
        if normalize_text(token) in normalize_text(candidate.text)
    )
    technical_score = (
        len(exact_tokens) / len(request.technical_tokens)
        if request.technical_tokens
        else 0.0
    )
    version_match = _ranking_version_status(
        request.requested_version, candidate.version, candidate.text
    )
    version_score = 1.0 if version_match == "exact" else 0.5 if version_match == "near" else 0.0
    trust = _ranking_source_trust(candidate.source_type)
    topic_score = _weighted_average(
        (0.24, feature_score, bool(request.query_profile.features)),
        (0.16, component_score, bool(request.query_profile.components)),
        (0.11, product_score, bool(request.requested_product)),
        (0.10, operation_score, bool(request.query_profile.operations)),
    )
    weighted = [
        (0.24, feature_score, bool(request.query_profile.features)),
        (0.16, component_score, bool(request.query_profile.components)),
        (0.11, product_score, bool(request.requested_product)),
        (0.10, entity_score, bool(query_entities)),
        (0.10, operation_score, bool(request.query_profile.operations)),
        (0.09, intent_score, bool(request.query_profile.intents)),
        (0.08, technical_score, bool(request.technical_tokens)),
        (0.02, trust, True),
        (0.02, version_score, bool(request.requested_version)),
    ]
    if candidate.lexical_score is not None or candidate.semantic_score is not None:
        weighted.extend(
            (
                (0.05, _clamp(candidate.lexical_score or 0.0), candidate.lexical_score is not None),
                (0.03, _clamp(candidate.semantic_score or 0.0), candidate.semantic_score is not None),
            )
        )
    else:
        weighted.append((0.08, _clamp(candidate.base_search_score), True))
    penalty = _ranking_conflict_penalty(comparison, product_match, version_match)
    from .phase175 import profiles_overlap
    explicitly_excluded = profiles_overlap(request.excluded_profile, candidate.profile)
    exclusion_penalty = -0.90 if explicitly_excluded and not candidate.manually_selected else 0.0
    coverage = _ranking_coverage(
        request.query_profile, candidate.profile, comparison, candidate.text
    )
    return TopicEntityRankingAssessment(
        candidate_index=candidate.candidate_index,
        candidate_id=candidate.candidate_id,
        final_score=_clamp(_weighted_average(*weighted) + penalty + exclusion_penalty),
        topic_score=topic_score,
        product_score=product_score,
        component_score=component_score,
        feature_score=feature_score,
        operation_score=operation_score,
        intent_score=intent_score,
        entity_score=entity_score,
        technical_token_score=technical_score,
        base_search_score=_clamp(candidate.base_search_score),
        lexical_score=candidate.lexical_score,
        semantic_score=candidate.semantic_score,
        source_trust_score=trust,
        version_score=version_score,
        conflict_penalty=penalty,
        exclusion_penalty=exclusion_penalty,
        explicitly_excluded=explicitly_excluded,
        product_match=product_match,
        version_match=version_match,
        topic_conflict=comparison.topic_conflict,
        has_topic_match=comparison.has_topic_match,
        conflict_kinds=comparison.conflict_kinds,
        coverage=coverage,
        exact_technical_tokens=exact_tokens,
        text_fingerprint=hashlib.sha256(normalize_text(candidate.text).encode("utf-8")).hexdigest(),
        selection_reason=(
            "Explicitly excluded topic retained because the source was manually selected"
            if explicitly_excluded and candidate.manually_selected
            else f"Explicitly excluded topic; penalty={exclusion_penalty:.2f}"
            if explicitly_excluded
            else _selection_reason(comparison, coverage, penalty)
        ),
    )


def _ranking_conflict_penalty(
    comparison: TopicConflictAssessment,
    product_match: bool | None,
    version_match: str,
) -> float:
    penalty = -0.65 if product_match is False else 0.0
    if "Feature" in comparison.conflict_kinds:
        penalty -= 0.55
    elif "Component" in comparison.conflict_kinds:
        penalty -= 0.30
    elif comparison.no_topic_match:
        penalty -= 0.20
    if version_match == "mismatch":
        penalty -= 0.18
    return penalty


def _ranking_coverage(
    query: TopicEntityProfile,
    evidence: TopicEntityProfile,
    comparison: TopicConflictAssessment,
    text: str,
) -> frozenset[TopicCoverage]:
    values: set[TopicCoverage] = set()
    normalized = normalize_text(text)
    if query.features and evidence.features and comparison.has_topic_match:
        if _has_any(normalized, "概要", "とは", "overview", "what is", "definition"):
            values.add(TopicCoverage.OVERVIEW)
        if _has_any(normalized, "用途", "目的", "使用する", "used for", "purpose", "use case"):
            values.add(TopicCoverage.PURPOSE)
        if _has_any(normalized, "設定", "作成", "手順", "configure", "configuration", "setup", "create", "step"):
            values.add(TopicCoverage.SETUP)
        if _has_any(normalized, "関連付け", "紐付け", "associate", "association", "link", "project", "qac"):
            values.add(TopicCoverage.ASSOCIATION)
        if _has_any(normalized, "確認", "検証", "状態", "verify", "verification", "confirm", "status", "check"):
            values.add(TopicCoverage.VERIFICATION)

    upload_workflow = "Build upload" in query.features or (
        "Upload" in query.operations
        and any(normalize_text(item) == "validate" for item in query.components)
    )
    if upload_workflow and not comparison.topic_conflict:
        has_command = _has_any(normalized, "qacli", "command", "コマンド")
        if has_command and _has_any(normalized, "upload", "アップロード", "validate build"):
            values.add(TopicCoverage.UPLOAD_COMMAND)
        if _OPTION.search(text) or _has_any(normalized, "option", "parameter", "オプション", "引数"):
            values.add(TopicCoverage.COMMAND_OPTIONS)
        if _has_any(normalized, "auth", "authentication", "login", "credential", "token", "認証", "ログイン"):
            values.add(TopicCoverage.AUTHENTICATION)
        if _has_any(normalized, "project", "associate", "association", "関連付け", "紐付け"):
            values.add(TopicCoverage.PROJECT_ASSOCIATION)
        if has_command or _has_any(normalized, "execute", "run", "実行"):
            values.add(TopicCoverage.EXECUTION)
        if "validate" in normalized and _has_any(normalized, "confirm", "verify", "portal", "確認", "表示"):
            values.add(TopicCoverage.VALIDATE_VERIFICATION)
        if _has_any(normalized, "fail", "error", "check", "失敗", "エラー", "トラブルシュート"):
            values.add(TopicCoverage.FAILURE_CHECKS)
    return frozenset(values)


def _build_insufficient_reasons(
    request: TopicEntityRankingRequest,
    assessed: tuple[TopicEntityRankingAssessment, ...],
    selected: tuple[TopicEntityRankingAssessment, ...],
    coverage: frozenset[TopicCoverage],
    candidate_by_index: dict[int, TopicEntityRankingCandidate],
) -> tuple[str, ...]:
    reasons: list[str] = []
    specific_topic = bool(request.query_profile.features or request.query_profile.components)
    if specific_topic and not any(item.has_topic_match for item in selected):
        reasons.append("NoTopicMatch")
    if not selected and any(item.topic_conflict for item in assessed):
        reasons.append("TopicConflict")
    if assessed and all(item.product_match is False for item in assessed):
        reasons.append("ProductMismatch")
    feature_question = bool(request.query_profile.features)
    if feature_question and "Overview" in request.query_profile.intents and TopicCoverage.OVERVIEW not in coverage:
        reasons.append("MissingOverview")
    setup_requested = any(
        item in {"HowTo", "Configuration"} for item in request.query_profile.intents
    )
    if feature_question and setup_requested and TopicCoverage.SETUP not in coverage:
        reasons.append("MissingSetupProcedure")
    if feature_question and setup_requested and TopicCoverage.VERIFICATION not in coverage:
        reasons.append("MissingVerification")
    if feature_question and len(coverage) < 3:
        reasons.append("LowCoverage")
    upload_workflow = "Build upload" in request.query_profile.features or (
        "Upload" in request.query_profile.operations
        and any(
            normalize_text(item) == "validate"
            for item in request.query_profile.components
        )
    )
    if (
        upload_workflow
        and "Command" in request.query_profile.intents
        and TopicCoverage.UPLOAD_COMMAND not in coverage
    ):
        reasons.append("MissingCommand")
    if upload_workflow and "HowTo" in request.query_profile.intents and (
        TopicCoverage.EXECUTION not in coverage
        or TopicCoverage.VALIDATE_VERIFICATION not in coverage
    ):
        reasons.append("MissingSetupProcedure")
    if selected and max(item.final_score for item in selected) < 0.25:
        reasons.append("LowConfidence")
    if request.requested_version and selected and all(
        item.version_match not in {"exact", "near"} for item in selected
    ):
        reasons.append("VersionMismatch")
    versions = {
        normalize_text(candidate_by_index[item.candidate_index].version)
        for item in selected
        if candidate_by_index[item.candidate_index].version
    }
    if len(versions) > 1:
        reasons.append("ConflictingEvidence")
    return tuple(dict.fromkeys(reasons))


def _match_score(query: tuple[str, ...], evidence: tuple[str, ...]) -> float:
    if not query:
        return 0.0
    evidence_values = {normalize_text(value) for value in evidence}
    return sum(normalize_text(value) in evidence_values for value in query) / len(query)


def _matched_entity_score(
    query: tuple[TopicEntity, ...], matched: tuple[TopicEntity, ...]
) -> float:
    if not query:
        return 0.0
    matched_values = {(item.kind, item.normalized_value) for item in matched}
    return sum((item.kind, item.normalized_value) in matched_values for item in query) / len(query)


def _weighted_average(*values: tuple[float, float, bool]) -> float:
    applicable = [item for item in values if item[2]]
    total_weight = sum(item[0] for item in applicable)
    if total_weight <= 0:
        return 0.0
    return sum(weight * _clamp(score) for weight, score, _ in applicable) / total_weight


def _product_match(requested: str | None, actual: str | None) -> bool | None:
    if not requested or not actual:
        return None
    return normalize_text(requested).replace(" ", "") == normalize_text(actual).replace(" ", "")


def _ranking_version_status(
    requested: str | None, structured: str | None, text: str
) -> str:
    if not requested:
        return "not_requested"
    expected = _VERSION.search(requested)
    actual = _VERSION.search(structured or "") or _VERSION.search(text)
    if not expected or not actual:
        return "unknown"
    left = expected.group(0).casefold().removeprefix("v")
    right = actual.group(0).casefold().removeprefix("v")
    if left == right:
        return "exact"
    return "near" if left.split(".")[:2] == right.split(".")[:2] else "mismatch"


def _ranking_source_trust(source_type: str) -> float:
    normalized = normalize_text(source_type).replace("_", "").replace("-", "")
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


def _selection_reason(
    comparison: TopicConflictAssessment,
    coverage: frozenset[TopicCoverage],
    penalty: float,
) -> str:
    if comparison.topic_conflict:
        return f"Topic conflict: {', '.join(comparison.conflict_kinds)}; penalty={penalty:.2f}"
    match = "topic matched" if comparison.has_topic_match else "topic not confirmed"
    return match if not coverage else f"{match}; coverage={','.join(sorted(item.value for item in coverage))}"


def _has_any(normalized_text: str, *terms: str) -> bool:
    return any(normalize_text(term) in normalized_text for term in terms)


def _clamp(value: float) -> float:
    return max(0.0, min(1.0, value))


def _match_aliases(
    normalized_text: str, definitions: Iterable[AliasDefinition]
) -> tuple[str, ...]:
    matched: list[str] = []
    for definition in definitions:
        if not definition.canonical_name.strip():
            continue
        aliases = (definition.canonical_name, *definition.aliases)
        if any(_contains_alias(normalized_text, normalize_text(alias)) for alias in aliases):
            _append_unique(matched, definition.canonical_name.strip())
    return tuple(matched)


def _match_named_terms(
    normalized_text: str, definitions: Iterable[tuple[str, tuple[str, ...]]]
) -> tuple[str, ...]:
    return tuple(
        name
        for name, terms in definitions
        if any(_contains_alias(normalized_text, normalize_text(term)) for term in terms)
    )


def _contains_alias(normalized_text: str, normalized_alias: str) -> bool:
    if not normalized_alias:
        return False
    if (
        normalized_alias.isascii()
        and normalized_alias[0].isalnum()
        and normalized_alias[-1].isalnum()
    ):
        return bool(
            re.search(
                rf"(?<![a-z0-9]){re.escape(normalized_alias)}(?![a-z0-9])",
                normalized_text,
            )
        )
    return normalized_alias in normalized_text


def _add_entity(entities: list[TopicEntity], kind: EntityKind, value: str) -> None:
    normalized = normalize_entity_value(kind, value)
    if not normalized or any(
        item.kind is kind and item.normalized_value == normalized for item in entities
    ):
        return
    entities.append(TopicEntity(kind=kind, value=value.strip(), normalized_value=normalized))


def _add_known_entities(
    entities: list[TopicEntity],
    normalized_text: str,
    kind: EntityKind,
    definitions: Iterable[tuple[str, tuple[str, ...]]],
) -> None:
    for canonical, aliases in definitions:
        if any(_contains_alias(normalized_text, normalize_text(alias)) for alias in aliases):
            _add_entity(entities, kind, canonical)


def _intersect_values(left: tuple[str, ...], right: tuple[str, ...]) -> tuple[str, ...]:
    right_values = {normalize_text(value) for value in right}
    return tuple(value for value in left if normalize_text(value) in right_values)


def _intersect_entities(
    left: tuple[TopicEntity, ...], right: tuple[TopicEntity, ...]
) -> tuple[TopicEntity, ...]:
    right_values = {
        (item.kind, normalize_entity_value(item.kind, item.value)) for item in right
    }
    return tuple(
        item
        for item in left
        if (item.kind, normalize_entity_value(item.kind, item.value)) in right_values
    )


def _add_conflict(
    conflicts: list[str],
    kind: str,
    query_values: tuple[str, ...],
    evidence_values: tuple[str, ...],
    matches: tuple[str, ...],
) -> None:
    if query_values and evidence_values and not matches:
        conflicts.append(kind)


def _append_unique(values: list[str], value: str) -> None:
    normalized = normalize_text(value)
    if not any(normalize_text(existing) == normalized for existing in values):
        values.append(value)
