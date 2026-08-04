from __future__ import annotations

from dataclasses import asdict, dataclass, field
from pathlib import PurePath, PureWindowsPath
from typing import Any, Mapping


def _value(data: Mapping[str, Any], snake_name: str, camel_name: str) -> Any:
    if snake_name in data:
        return data[snake_name]
    return data.get(camel_name)


def _source_name(source_path: str | None) -> str | None:
    if not source_path:
        return None
    windows_name = PureWindowsPath(source_path).name
    return windows_name or PurePath(source_path).name or None


@dataclass(frozen=True, slots=True)
class DocumentMetadata:
    product_id: str | None = None
    product_name: str | None = None
    support_id: str | None = None
    information_type: str | None = None
    document_name: str | None = None
    source_path: str | None = None
    target_version: str | None = None
    os: str | None = None
    error_code: str | None = None
    command: str | None = None
    created_or_updated_at: str | None = None
    source_type: str | None = None
    confidence: float | None = None
    question: str | None = None
    cause: str | None = None
    resolution: str | None = None
    manufacturer_reply: str | None = None
    customer_final_reply: str | None = None

    def __post_init__(self) -> None:
        if self.confidence is not None and not 0.0 <= self.confidence <= 1.0:
            raise ValueError("confidence must be between 0.0 and 1.0")

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "DocumentMetadata":
        return cls(
            product_id=_value(data, "product_id", "productId"),
            product_name=_value(data, "product_name", "productName"),
            support_id=_value(data, "support_id", "supportId"),
            information_type=_value(data, "information_type", "informationType"),
            document_name=_value(data, "document_name", "documentName"),
            source_path=_value(data, "source_path", "sourcePath"),
            target_version=_value(data, "target_version", "targetVersion"),
            os=data.get("os"),
            error_code=_value(data, "error_code", "errorCode"),
            command=data.get("command"),
            created_or_updated_at=_value(
                data, "created_or_updated_at", "createdOrUpdatedAt"
            ),
            source_type=_value(data, "source_type", "sourceType"),
            confidence=data.get("confidence"),
            question=data.get("question"),
            cause=data.get("cause"),
            resolution=data.get("resolution"),
            manufacturer_reply=_value(
                data, "manufacturer_reply", "manufacturerReply"
            ),
            customer_final_reply=_value(
                data, "customer_final_reply", "customerFinalReply"
            ),
        )

    def to_dict(self, *, include_source_path: bool = False) -> dict[str, Any]:
        result = asdict(self)
        source_path = result.pop("source_path")
        if include_source_path:
            result["source_path"] = source_path
        else:
            result["source_name"] = _source_name(source_path)
        return result


@dataclass(frozen=True, slots=True)
class Document:
    document_id: str
    text: str
    metadata: DocumentMetadata = field(default_factory=DocumentMetadata)

    def __post_init__(self) -> None:
        if not self.document_id.strip():
            raise ValueError("document_id is required")
        if not isinstance(self.text, str):
            raise TypeError("text must be a string")

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "Document":
        document_id = _value(data, "document_id", "documentId")
        text = data.get("text")
        metadata = data.get("metadata", {})
        if not isinstance(document_id, str) or not isinstance(text, str):
            raise ValueError("document_id and text must be strings")
        if not isinstance(metadata, Mapping):
            raise ValueError("metadata must be an object")
        return cls(document_id, text, DocumentMetadata.from_dict(metadata))


@dataclass(frozen=True, slots=True)
class Chunk:
    chunk_id: str
    document_id: str
    chunk_index: int
    strategy: str
    text: str
    metadata: DocumentMetadata
    section_title: str | None = None

    def to_public_dict(self) -> dict[str, Any]:
        return {
            "chunk_id": self.chunk_id,
            "document_id": self.document_id,
            "chunk_index": self.chunk_index,
            "strategy": self.strategy,
            "section_title": self.section_title,
            "text": self.text,
            "metadata": self.metadata.to_dict(include_source_path=False),
        }


@dataclass(frozen=True, slots=True)
class EvaluationCase:
    query_id: str
    product: str
    query: str
    target_version: str | None = None
    expected_document_ids: tuple[str, ...] = ()
    expected_support_ids: tuple[str, ...] = ()
    required_terms: tuple[str, ...] = ()
    excluded_terms: tuple[str, ...] = ()
    notes: str | None = None

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "EvaluationCase":
        required = ("query_id", "product", "query")
        if any(not isinstance(data.get(name), str) for name in required):
            raise ValueError("query_id, product, and query must be strings")

        def string_tuple(name: str) -> tuple[str, ...]:
            value = data.get(name, [])
            if not isinstance(value, list) or not all(
                isinstance(item, str) for item in value
            ):
                raise ValueError(f"{name} must be an array of strings")
            return tuple(value)

        return cls(
            query_id=data["query_id"],
            product=data["product"],
            query=data["query"],
            target_version=_value(data, "target_version", "targetVersion"),
            expected_document_ids=string_tuple("expected_document_ids"),
            expected_support_ids=string_tuple("expected_support_ids"),
            required_terms=string_tuple("required_terms"),
            excluded_terms=string_tuple("excluded_terms"),
            notes=data.get("notes"),
        )
