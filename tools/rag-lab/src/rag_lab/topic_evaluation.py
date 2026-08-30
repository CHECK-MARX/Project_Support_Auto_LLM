"""Independent topic evaluation for the synthetic Phase 22 corpus."""

from __future__ import annotations

from dataclasses import dataclass
import json
from pathlib import Path
from typing import Iterable

from .models import EvaluationCase
from .search import SearchResult


def _key(value: str | None) -> str:
    return (value or "").strip().casefold()


@dataclass(frozen=True, slots=True)
class DocumentTopics:
    product: str
    feature: str | None
    operation: str | None
    intent: str | None
    topics: frozenset[str]


@dataclass(frozen=True, slots=True)
class TopicPolicy:
    product: str
    feature: str | None
    operation: str | None
    language: str | None
    required_topics: frozenset[str]
    allowed_related_topics: frozenset[str]
    forbidden_topics: frozenset[str]

    def specificity(self) -> int:
        return sum(value is not None for value in (self.feature, self.operation, self.language))

    def applies_to(self, case: EvaluationCase) -> bool:
        return (
            _key(self.product) == _key(case.product)
            and (self.feature is None or _key(self.feature) == _key(case.feature))
            and (self.operation is None or _key(self.operation) == _key(case.operation))
            and (self.language is None or _key(self.language) == _key(case.language))
        )


@dataclass(frozen=True, slots=True)
class TopicMetadata:
    documents: dict[str, DocumentTopics]
    policies: tuple[TopicPolicy, ...]

    def policy_for(self, case: EvaluationCase) -> TopicPolicy:
        matches = sorted(
            (item for item in self.policies if item.applies_to(case)),
            key=lambda item: item.specificity(),
            reverse=True,
        )
        if matches:
            return matches[0]
        return TopicPolicy(
            product=case.product,
            feature=case.feature,
            operation=case.operation,
            language=case.language,
            required_topics=frozenset(filter(None, (case.product, case.feature, case.operation))),
            allowed_related_topics=frozenset(),
            forbidden_topics=frozenset(),
        )


def load_topic_metadata(path: str | Path) -> TopicMetadata:
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(raw, dict) or raw.get("schemaVersion") != 1:
        raise ValueError("topic metadata schemaVersion=1 is required")
    documents: dict[str, DocumentTopics] = {}
    for document_id, value in raw.get("documents", {}).items():
        if not isinstance(document_id, str) or not isinstance(value, dict):
            raise ValueError("topic metadata documents must be an object")
        topics = value.get("topics", [])
        if not isinstance(topics, list) or not all(isinstance(item, str) for item in topics):
            raise ValueError("topic metadata document topics must be strings")
        documents[document_id] = DocumentTopics(
            product=str(value["product"]),
            feature=value.get("feature"),
            operation=value.get("operation"),
            intent=value.get("intent"),
            topics=frozenset(topics),
        )
    policies: list[TopicPolicy] = []
    for value in raw.get("policies", []):
        if not isinstance(value, dict):
            raise ValueError("topic metadata policies must be objects")
        def values(name: str) -> frozenset[str]:
            items = value.get(name, [])
            if not isinstance(items, list) or not all(isinstance(item, str) for item in items):
                raise ValueError(f"topic metadata {name} must be an array of strings")
            return frozenset(items)
        policies.append(TopicPolicy(
            product=str(value["product"]),
            feature=value.get("feature"),
            operation=value.get("operation"),
            language=value.get("language"),
            required_topics=values("requiredTopics"),
            allowed_related_topics=values("allowedRelatedTopics"),
            forbidden_topics=values("forbiddenTopics"),
        ))
    return TopicMetadata(documents, tuple(policies))


def evaluate_topic_results(
    case: EvaluationCase,
    results: Iterable[SearchResult],
    metadata: TopicMetadata,
) -> dict[str, float | int]:
    policy = metadata.policy_for(case)
    ranked = list(results)
    top3 = ranked[:3]
    top5 = ranked[:5]
    discovered = set().union(*(metadata.documents.get(item.chunk.document_id, DocumentTopics("", None, None, None, frozenset())).topics for item in ranked))
    required_recall = (
        len(policy.required_topics & discovered) / len(policy.required_topics)
        if policy.required_topics else 1.0
    )

    def forbidden_count(items: list[SearchResult]) -> int:
        return sum(bool(metadata.documents.get(item.chunk.document_id, DocumentTopics("", None, None, None, frozenset())).topics & policy.forbidden_topics) for item in items)

    def product_mismatch(items: list[SearchResult]) -> int:
        return sum(_key(metadata.documents.get(item.chunk.document_id, DocumentTopics("", None, None, None, frozenset())).product) != _key(case.product) for item in items)

    def feature_mismatch(items: list[SearchResult]) -> int:
        if not case.feature:
            return 0
        allowed = {_key(value) for value in policy.allowed_related_topics}
        return sum(
            bool(label.feature) and _key(label.feature) != _key(case.feature) and _key(label.feature) not in allowed
            for item in items
            for label in [metadata.documents.get(item.chunk.document_id, DocumentTopics("", None, None, None, frozenset()))]
        )

    def operation_mismatch(items: list[SearchResult]) -> int:
        if not case.operation:
            return 0
        allowed = {_key(value) for value in policy.allowed_related_topics}
        return sum(
            bool(label.operation) and _key(label.operation) != _key(case.operation) and _key(label.operation) not in allowed
            for item in items
            for label in [metadata.documents.get(item.chunk.document_id, DocumentTopics("", None, None, None, frozenset()))]
        )

    return {
        "required_topic_recall": required_recall,
        "forbidden_topic_top3_count": forbidden_count(top3),
        "forbidden_topic_top5_count": forbidden_count(top5),
        "product_mismatch_top3_count": product_mismatch(top3),
        "feature_mismatch_top3_count": feature_mismatch(top3),
        "operation_mismatch_top3_count": operation_mismatch(top3),
    }


def aggregate_topic_evaluations(items: Iterable[dict[str, float | int]]) -> dict[str, float | int]:
    values = list(items)
    if not values:
        return {
            "required_topic_recall": 0.0,
            "forbidden_topic_top3_count": 0,
            "forbidden_topic_top5_count": 0,
            "product_mismatch_top3_count": 0,
            "feature_mismatch_top3_count": 0,
            "operation_mismatch_top3_count": 0,
        }
    return {
        "required_topic_recall": sum(float(item["required_topic_recall"]) for item in values) / len(values),
        "forbidden_topic_top3_count": sum(int(item["forbidden_topic_top3_count"]) for item in values),
        "forbidden_topic_top5_count": sum(int(item["forbidden_topic_top5_count"]) for item in values),
        "product_mismatch_top3_count": sum(int(item["product_mismatch_top3_count"]) for item in values),
        "feature_mismatch_top3_count": sum(int(item["feature_mismatch_top3_count"]) for item in values),
        "operation_mismatch_top3_count": sum(int(item["operation_mismatch_top3_count"]) for item in values),
    }
