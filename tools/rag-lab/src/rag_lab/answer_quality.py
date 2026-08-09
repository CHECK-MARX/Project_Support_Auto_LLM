from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Iterable

from .topic_entity_ranking import (
    AliasDefinition,
    EntityAliasDefinition,
    EntityKind,
    TopicEntityCatalog,
    TopicEntityProfile,
    compare_topic_profiles,
    extract_topic_entity_profile,
    normalize_text,
)


CUSTOMER_READY = "CustomerReady"
NEEDS_REVIEW = "NeedsReview"
INSUFFICIENT_EVIDENCE = "InsufficientEvidence"
BLOCKED = "Blocked"


@dataclass(frozen=True, slots=True)
class AnswerQualityThresholds:
    minimum_directness: float = 0.65
    minimum_grounding: float = 0.80
    minimum_topic_alignment: float = 0.75
    minimum_coverage: float = 0.80
    minimum_technical_fidelity: float = 1.0
    minimum_actionability: float = 0.65
    minimum_customer_readiness: float = 0.65
    insufficient_grounding: float = 0.30
    insufficient_coverage: float = 0.34


@dataclass(frozen=True, slots=True)
class AnswerQualityEvidence:
    source_id: str
    source_type: str
    text: str
    product_name: str | None = None
    version: str | None = None


@dataclass(frozen=True, slots=True)
class AnswerQualityInput:
    question: str
    answer: str
    evidence: tuple[AnswerQualityEvidence, ...]
    product_name: str | None = None
    requested_version: str | None = None
    existing_insufficient_reasons: tuple[str, ...] = ()
    catalog: TopicEntityCatalog | None = None


@dataclass(frozen=True, slots=True)
class TechnicalClaim:
    kind: str
    value: str
    normalized_value: str
    is_major: bool


_COMMAND = re.compile(r"\bqacli(?:\s+[a-z][a-z0-9_.-]*){1,4}", re.IGNORECASE)
_OPTION = re.compile(r"(?<![A-Za-z0-9_])--[A-Za-z][A-Za-z0-9_-]*")
_API = re.compile(r"\b[A-Z][A-Za-z0-9_.-]*\s+API\b")
_SETTING = re.compile(r"\b[A-Z][A-Z0-9_]{2,}\s*=\s*[A-Za-z0-9_.-]+\b")
_FILE = re.compile(
    r"(?<![A-Za-z0-9_.-])[A-Za-z0-9_.-]+\.(?:json|xml|ya?ml|conf|cfg|ini|txt|log|pdf|qac|cct)(?=$|[^A-Za-z0-9_.-])",
    re.IGNORECASE,
)
_WINDOWS_PATH = re.compile(r"\b[A-Za-z]:\\[^\s\"'<>|]+")
_UNIX_PATH = re.compile(r"(?<![A-Za-z0-9_.-])/(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+")
_ERROR = re.compile(r"\b[A-Z]{2,10}-\d{2,8}\b")
_VERSION = re.compile(r"(?:version|ver\.?|v|バージョン)\s*[:：]?\s*\d+(?:\.\d+){1,3}", re.IGNORECASE)
_PORT = re.compile(r"(?:port|ポート)\s*[:：]?\s*\d{2,5}", re.IGNORECASE)
_STEP = re.compile(r"(?m)^\s*(?:\d+[.)、]|[-*・])")
_JAPANESE = re.compile(r"[\u3040-\u30ff\u3400-\u9fff]")
_POLITE = re.compile(r"(?:です|ます|ください|いたします)[。\s]", re.IGNORECASE)
_SAFE_ACRONYMS = {"api", "cli", "gui", "http", "https", "os"}
_INTERNAL_MARKERS = (
    "[RAG Evidence]", "Evidence score", "Selection reason", "fallback",
    "TopK", "InsufficientEvidence", "CompletedWithFallback", "Phase 14",
    "Phase 15", "Phase 16", "Phase 17", "RAG内部", "内部スコア",
)
_INSUFFICIENT_MARKERS = (
    "根拠不足", "情報不足", "確認できません", "判断できません",
    "断定できません", "insufficient evidence",
)


def create_support_catalog(product_name: str | None = None) -> TopicEntityCatalog:
    return TopicEntityCatalog(
        products=(AliasDefinition(product_name),) if product_name else (),
        components=(AliasDefinition("Validate", ("Perforce Validate",)),),
        features=(
            AliasDefinition("Stream", ("ストリーム",)),
            AliasDefinition("License", ("ライセンス",)),
            AliasDefinition("IDE Plugin", ("IDEプラグイン", "Eclipse Plugin")),
            AliasDefinition("Build upload", ("validate build", "build upload", "解析結果をアップロード")),
        ),
        objects=(AliasDefinition("Analysis result", ("解析結果",)),),
        entities=(
            EntityAliasDefinition(EntityKind.COMMAND, "qacli validate build", ("validate build",)),
        ),
    )


def extract_technical_claims(
    text: str | None,
    catalog: TopicEntityCatalog | None = None,
) -> tuple[TechnicalClaim, ...]:
    source = text or ""
    claims: list[TechnicalClaim] = []
    for kind, pattern, major in (
        ("Command", _COMMAND, True),
        ("Option", _OPTION, True),
        ("Api", _API, True),
        ("Setting", _SETTING, True),
        ("Path", _WINDOWS_PATH, True),
        ("Path", _UNIX_PATH, True),
        ("File", _FILE, False),
        ("ErrorCode", _ERROR, True),
        ("Version", _VERSION, True),
        ("Port", _PORT, True),
    ):
        for match in pattern.finditer(source):
            _add_claim(claims, kind, match.group(0), major)

    profile = extract_topic_entity_profile(source, catalog or TopicEntityCatalog())
    for entity in profile.entities:
        if entity.kind in {EntityKind.PRODUCT, EntityKind.FEATURE}:
            _add_claim(claims, "ProductFeature", entity.value, False)
        elif entity.kind in {EntityKind.OPERATING_SYSTEM, EntityKind.SERVER_TYPE}:
            _add_claim(claims, entity.kind.value, entity.value, False)

    unique: dict[tuple[str, str], TechnicalClaim] = {}
    for claim in claims:
        if claim.normalized_value not in _SAFE_ACRONYMS:
            unique.setdefault((claim.kind, claim.normalized_value), claim)
    return tuple(unique.values())


def evaluate_answer_quality(
    value: AnswerQualityInput,
    thresholds: AnswerQualityThresholds | None = None,
) -> dict[str, object]:
    rules = thresholds or AnswerQualityThresholds()
    catalog = value.catalog or create_support_catalog(value.product_name)
    query_profile = extract_topic_entity_profile(value.question, catalog)
    answer_profile = extract_topic_entity_profile(value.answer, catalog)
    comparison = compare_topic_profiles(query_profile, answer_profile)
    topic_alignment = _topic_alignment(query_profile, comparison)

    answer_claims = extract_technical_claims(value.answer, catalog)
    support_claims = extract_technical_claims(
        "\n".join((value.question, *(item.text for item in value.evidence))), catalog
    )
    supported = {(item.kind, item.normalized_value) for item in support_claims}
    unsupported = tuple(
        item for item in answer_claims
        if (item.kind, item.normalized_value) not in supported
    )
    grounding = _grounding(value, len(answer_claims), len(unsupported), topic_alignment)
    technical_fidelity = 1.0 if not answer_claims else _clamp(1 - len(unsupported) / len(answer_claims))
    required = _required_coverage(query_profile)
    observed = _observed_coverage(value.answer, answer_claims)
    coverage = 1.0 if not required else len(required & observed) / len(required)
    directness = _directness(value.answer, topic_alignment, coverage)
    actionability = _actionability(query_profile, value.answer, observed)
    leakage_count = sum(marker.casefold() in value.answer.casefold() for marker in _INTERNAL_MARKERS)
    customer_readiness = _customer_readiness(value.answer, leakage_count)
    conflict_count = _conflicts(value, query_profile, catalog)

    blocking: list[str] = []
    warnings: list[str] = []
    if comparison.topic_conflict:
        blocking.append("TopicConflict")
    if any(item.is_major for item in unsupported):
        blocking.append("MajorUnsupportedTechnicalClaim")
    if leakage_count:
        warnings.append("InternalLeakage")
    if conflict_count:
        warnings.append("EvidenceConflict")
    if unsupported:
        warnings.append("UnsupportedTechnicalClaim")

    if blocking:
        decision = BLOCKED
    elif (
        not value.evidence
        or value.existing_insufficient_reasons
        or grounding < rules.insufficient_grounding
        or coverage < rules.insufficient_coverage
    ):
        decision = INSUFFICIENT_EVIDENCE
    elif conflict_count or leakage_count:
        decision = NEEDS_REVIEW
    else:
        ready = (
            directness >= rules.minimum_directness
            and grounding >= rules.minimum_grounding
            and topic_alignment >= rules.minimum_topic_alignment
            and coverage >= rules.minimum_coverage
            and technical_fidelity >= rules.minimum_technical_fidelity
            and actionability >= rules.minimum_actionability
            and customer_readiness >= rules.minimum_customer_readiness
        )
        decision = CUSTOMER_READY if ready else NEEDS_REVIEW

    return {
        "directness": _round(directness),
        "grounding": _round(grounding),
        "topicAlignment": _round(topic_alignment),
        "coverage": _round(coverage),
        "technicalFidelity": _round(technical_fidelity),
        "unsupportedClaimCount": len(unsupported),
        "unsupportedTechnicalClaims": [
            {"kind": item.kind, "value": item.value, "isMajor": item.is_major}
            for item in unsupported
        ],
        "conflictCount": conflict_count,
        "actionability": _round(actionability),
        "customerReadiness": _round(customer_readiness),
        "internalLeakageCount": leakage_count,
        "blockingReasons": list(dict.fromkeys(blocking)),
        "warnings": list(dict.fromkeys(warnings)),
        "decision": decision,
    }


def _normalize_claim(kind: str, value: str) -> str:
    normalized = normalize_text(value).strip(" .,():;[]{}「」［］")
    if kind == "Command":
        normalized = re.sub(r"^qacli\s+", "", normalized, flags=re.IGNORECASE).strip()
    elif kind == "Version":
        normalized = re.sub(r"^(?:version|ver\.?|v|バージョン)\s*[:：]?\s*", "", normalized, flags=re.IGNORECASE).strip()
    elif kind == "Port":
        normalized = re.sub(r"^(?:port|ポート)\s*[:：]?\s*", "", normalized, flags=re.IGNORECASE).strip()
    return re.sub(r"\s+", " ", normalized)


def _add_claim(claims: list[TechnicalClaim], kind: str, value: str, major: bool) -> None:
    normalized = _normalize_claim(kind, value)
    if normalized:
        claims.append(TechnicalClaim(kind, value.strip(), normalized, major))


def _topic_alignment(query: TopicEntityProfile, comparison) -> float:
    if comparison.topic_conflict:
        return 0.0
    if query.features or query.components:
        return 1.0 if comparison.has_topic_match else 0.25
    if query.products:
        return 1.0 if comparison.matched_products else 0.5
    return 1.0


def _grounding(value: AnswerQualityInput, claims: int, unsupported: int, alignment: float) -> float:
    if not value.evidence:
        return 0.0
    if claims:
        return _clamp(1 - unsupported / claims)
    return 1.0 if alignment >= 0.75 else 0.6 if alignment >= 0.5 else 0.3


def _directness(answer: str, alignment: float, coverage: float) -> float:
    if not answer.strip():
        return 0.0
    if any(marker.casefold() in answer.casefold() for marker in _INSUFFICIENT_MARKERS):
        return 0.25
    score = alignment * 0.65 + coverage * 0.35
    return _clamp(score * 0.5 if len(answer.strip()) < 20 else score)


def _required_coverage(query: TopicEntityProfile) -> set[str]:
    upload = "Upload" in query.operations or "Build upload" in query.features
    if upload:
        return {"Command", "Option", "Authentication", "Association", "Execution", "Verification"}
    if "Stream" in query.features:
        required: set[str] = set()
        if "Overview" in query.intents:
            required.add("Overview")
        if any(item in {"HowTo", "Configuration"} for item in query.intents):
            required.update(("Setup", "Association", "Verification"))
        return required
    if any(item in {"HowTo", "Configuration", "Command"} for item in query.intents):
        return {"Execution"}
    return set()


def _observed_coverage(answer: str, claims: Iterable[TechnicalClaim]) -> set[str]:
    items = tuple(claims)
    normalized = answer.casefold()
    observed: set[str] = set()
    if any(item.kind == "Command" for item in items): observed.add("Command")
    if any(item.kind == "Option" for item in items): observed.add("Option")
    if _has_any(normalized, "概要", "とは", "overview", "definition"): observed.add("Overview")
    if _has_any(normalized, "設定", "作成", "手順", "setup", "configure", "create"): observed.add("Setup")
    if _has_any(normalized, "認証", "ログイン", "token", "credential", "authentication"): observed.add("Authentication")
    if _has_any(normalized, "接続", "関連付け", "紐付け", "project", "associate", "connection"): observed.add("Association")
    if _has_any(normalized, "実行", "アップロード", "run", "execute", "upload"): observed.add("Execution")
    if _has_any(normalized, "確認", "検証", "表示", "verify", "confirm", "portal"): observed.add("Verification")
    return observed


def _actionability(query: TopicEntityProfile, answer: str, observed: set[str]) -> float:
    needs_action = any(item in {"HowTo", "Command", "Configuration"} for item in query.intents)
    if not needs_action:
        return 1.0
    if "Stream" in query.features:
        checks = (
            "Setup" in observed,
            "Association" in observed,
            "Verification" in observed,
        )
        return sum(checks) / len(checks)
    checks = (
        "Command" in observed or bool(_STEP.search(answer)),
        "Execution" in observed or _has_any(answer.casefold(), "実行", "操作", "run", "execute"),
        "Verification" in observed,
    )
    return sum(checks) / len(checks)


def _customer_readiness(answer: str, leakage: int) -> float:
    if not answer.strip():
        return 0.0
    score = 0.35 if _JAPANESE.search(answer) else 0.0
    score += 0.25 if "。" in answer or _POLITE.search(answer) else 0.0
    score += 0.25 if len(answer.strip()) >= 60 else 0.15 if len(answer.strip()) >= 30 else 0.0
    score += 0.15 if leakage == 0 else 0.0
    return _clamp(score)


def _conflicts(value: AnswerQualityInput, query: TopicEntityProfile, catalog: TopicEntityCatalog) -> int:
    count = sum(
        compare_topic_profiles(query, extract_topic_entity_profile(item.text, catalog)).topic_conflict
        for item in value.evidence
    )
    versions = {
        claim.normalized_value
        for item in value.evidence
        for claim in extract_technical_claims(item.text, catalog)
        if claim.kind == "Version"
    }
    return count + (1 if len(versions) > 1 else 0)


def _has_any(value: str, *terms: str) -> bool:
    return any(term.casefold() in value for term in terms)


def _clamp(value: float) -> float:
    return max(0.0, min(1.0, value))


def _round(value: float) -> float:
    return round(_clamp(value), 6)
