from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Iterable

from .topic_entity_ranking import (
    TopicEntityCatalog,
    TopicEntityProfile,
    extract_topic_entity_profile,
    normalize_text,
)


COMMAND = "Command"
OPTIONS = "Options"
AUTHENTICATION = "Authentication"
CONNECTION = "Connection"
UPLOAD_PROCEDURE = "UploadProcedure"
VALIDATE_VERIFICATION = "ValidateVerification"
TROUBLESHOOTING = "Troubleshooting"
STREAM_OVERVIEW = "StreamOverview"
PURPOSE = "Purpose"
STREAM_CREATION = "StreamCreation"
QAC_ASSOCIATION = "QacAssociation"
CONFIGURATION = "Configuration"
VERIFICATION = "Verification"

UPLOAD_REQUIREMENTS = (
    COMMAND, OPTIONS, AUTHENTICATION, CONNECTION, UPLOAD_PROCEDURE,
    VALIDATE_VERIFICATION, TROUBLESHOOTING,
)
STREAM_REQUIREMENTS = (
    STREAM_OVERVIEW, PURPOSE, STREAM_CREATION,
    QAC_ASSOCIATION, CONFIGURATION, VERIFICATION,
)

_EXCLUSION = re.compile(r"ではなく(?:て)?|ではない|ではありません|以外(?:は|の)?|を除く")
_COMMAND = re.compile(r"(?<![A-Za-z0-9_])qacli(?:\s+[A-Za-z0-9_.+/-]+){1,4}", re.IGNORECASE)
_OPTION = re.compile(r"(?<![A-Za-z0-9_])--[A-Za-z0-9][A-Za-z0-9_-]*")
_BOUNDARIES = "。！？!?\n\r;；"


@dataclass(frozen=True, slots=True)
class NegationAwareTopicAnalysis:
    primary_profile: TopicEntityProfile
    excluded_profile: TopicEntityProfile
    primary_text: str
    excluded_text_segments: tuple[str, ...]


def analyze_negation_aware_topics(
    text: str | None,
    catalog: TopicEntityCatalog,
) -> NegationAwareTopicAnalysis:
    if not text or not text.strip():
        return NegationAwareTopicAnalysis(TopicEntityProfile(), TopicEntityProfile(), "", ())
    masked = list(text)
    segments: list[str] = []
    for marker in _EXCLUSION.finditer(text):
        start = _clause_start(text, marker.start())
        segment = text[start:marker.start()].strip(" 、,。")
        if segment:
            segments.append(segment)
        masked[start:marker.end()] = " " * (marker.end() - start)
    primary_text = "".join(masked)
    return NegationAwareTopicAnalysis(
        primary_profile=extract_topic_entity_profile(primary_text, catalog),
        excluded_profile=_merge_profiles(
            extract_topic_entity_profile(segment, catalog) for segment in segments
        ),
        primary_text=primary_text,
        excluded_text_segments=tuple(segments),
    )


def profiles_overlap(excluded: TopicEntityProfile, candidate: TopicEntityProfile) -> bool:
    if _intersects(excluded.products, candidate.products): return True
    if _intersects(excluded.components, candidate.components): return True
    if _intersects(excluded.features, candidate.features): return True
    if _intersects(excluded.objects, candidate.objects): return True
    right = {(item.kind, item.normalized_value) for item in candidate.entities}
    return any((item.kind, item.normalized_value) in right for item in excluded.entities)


def required_coverage(question: str, profile: TopicEntityProfile) -> tuple[str, ...]:
    upload = (
        "Upload" in profile.operations
        or "Build upload" in profile.features
        or (_has_any(question, "Validate", "Validateへ") and _has_any(question, "upload", "アップロード"))
    )
    if upload:
        return UPLOAD_REQUIREMENTS
    if "Stream" in profile.features:
        return STREAM_REQUIREMENTS
    return ()


def observed_coverage(text: str | None) -> frozenset[str]:
    value = text or ""
    found: set[str] = set()
    has_validate = _has_any(value, "validate")
    has_qac = _has_any(value, "qac", "qacli", "perforce qac")
    has_stream = _has_any(value, "stream", "ストリーム")
    if _COMMAND.search(value): found.add(COMMAND)
    if _OPTION.search(value) or _has_any(value, "オプション", "option", "parameter"):
        found.update((OPTIONS, "Option"))
    if _has_any(value, "qacli auth", "認証", "ログイン", "credential", "token", "authentication"):
        found.add(AUTHENTICATION)
    if _has_any(value, "validate connect", "接続", "connection", "connect", "server url", "サーバーurl"):
        found.update((CONNECTION, "Association"))
    if has_validate and _has_any(value, "validate build", "validate ibuild", "アップロード", "upload", "build"):
        found.update((UPLOAD_PROCEDURE, "Execution"))
    if has_validate and _has_any(value, "確認", "verify", "verification", "portal", "build一覧", "表示"):
        found.update((VALIDATE_VERIFICATION, "Verification"))
    if _has_any(value, "失敗", "エラー", "原因", "対処", "ログ", "troubleshoot", "failure", "failed", "error", "check"):
        found.add(TROUBLESHOOTING)
    if has_stream and _has_any(value, "概要", "とは", "overview", "definition"): found.add(STREAM_OVERVIEW)
    if has_stream and _has_any(value, "用途", "目的", "利用", "purpose", "use case"): found.add(PURPOSE)
    if has_stream and _has_any(value, "作成", "create", "creation", "new stream"): found.add(STREAM_CREATION)
    if has_stream and has_qac and _has_any(value, "関連付け", "紐付け", "associate", "association", "link"): found.add(QAC_ASSOCIATION)
    if has_stream and _has_any(value, "設定", "構成", "configure", "configuration", "setup"): found.add(CONFIGURATION)
    if has_stream and _has_any(value, "確認", "検証", "verify", "verification", "check"): found.add(VERIFICATION)
    return frozenset(found)


def observed_evidence_coverage(texts: Iterable[str]) -> frozenset[str]:
    found: set[str] = set()
    for text in texts:
        found.update(observed_coverage(text))
    return frozenset(found)


def _clause_start(text: str, marker: int) -> int:
    for index in range(marker - 1, -1, -1):
        if text[index] in _BOUNDARIES:
            return index + 1
    return 0


def _merge_profiles(profiles: Iterable[TopicEntityProfile]) -> TopicEntityProfile:
    values = tuple(profiles)
    entities = {
        (item.kind, item.normalized_value): item
        for profile in values for item in profile.entities
    }
    return TopicEntityProfile(
        products=_unique(item for profile in values for item in profile.products),
        components=_unique(item for profile in values for item in profile.components),
        features=_unique(item for profile in values for item in profile.features),
        operations=_unique(item for profile in values for item in profile.operations),
        objects=_unique(item for profile in values for item in profile.objects),
        intents=_unique(item for profile in values for item in profile.intents),
        entities=tuple(entities.values()),
    )


def _unique(values: Iterable[str]) -> tuple[str, ...]:
    result: list[str] = []
    seen: set[str] = set()
    for value in values:
        key = normalize_text(value)
        if key not in seen:
            seen.add(key)
            result.append(value)
    return tuple(result)


def _intersects(left: tuple[str, ...], right: tuple[str, ...]) -> bool:
    values = {normalize_text(item) for item in right}
    return any(normalize_text(item) in values for item in left)


def _has_any(value: str, *terms: str) -> bool:
    return any(term.casefold() in value.casefold() for term in terms)
