from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

from .models import Document, EvaluationCase


class PathValidationError(ValueError):
    """Raised when an input or output path escapes the configured lab boundary."""


class DataValidationError(ValueError):
    """Raised when an input file is not valid UTF-8 JSON or has an invalid shape."""


def _is_within(candidate: Path, parent: Path) -> bool:
    try:
        candidate.relative_to(parent)
        return True
    except ValueError:
        return False


@dataclass(frozen=True, slots=True)
class LabPaths:
    lab_root: Path
    input_roots: tuple[Path, ...]
    output_root: Path

    @classmethod
    def create(
        cls,
        lab_root: str | Path,
        *,
        input_roots: Iterable[str | Path] = ("samples",),
        output_root: str | Path = "reports/generated",
    ) -> "LabPaths":
        root = Path(lab_root).resolve(strict=True)

        def from_lab(value: str | Path) -> Path:
            path = Path(value)
            return (path if path.is_absolute() else root / path).resolve(strict=False)

        inputs = tuple(from_lab(path) for path in input_roots)
        if not inputs:
            raise PathValidationError("at least one input root is required")
        generated_root = (root / "reports" / "generated").resolve(strict=False)
        output = from_lab(output_root)
        if not _is_within(output, generated_root):
            raise PathValidationError(
                "output root must be inside the RAG lab reports/generated directory"
            )
        return cls(root, inputs, output)

    def resolve_input(self, path: str | Path) -> Path:
        candidate_path = Path(path)
        candidate = (
            candidate_path
            if candidate_path.is_absolute()
            else self.lab_root / candidate_path
        ).resolve(strict=False)
        if not any(_is_within(candidate, root) for root in self.input_roots):
            raise PathValidationError("input path is outside the configured input roots")
        if not candidate.is_file():
            raise PathValidationError("input path does not identify an existing file")
        return candidate

    def resolve_output(self, relative_path: str | Path) -> Path:
        requested = Path(relative_path)
        candidate = (
            requested if requested.is_absolute() else self.output_root / requested
        ).resolve(strict=False)
        if not _is_within(candidate, self.output_root):
            raise PathValidationError("output path is outside reports/generated")
        return candidate

    def resolve_generated_input(self, path: str | Path) -> Path:
        candidate = self.resolve_output(path)
        if not candidate.is_file():
            raise PathValidationError(
                "generated input path does not identify an existing file"
            )
        return candidate


def _read_json_file(source: Path) -> Any:
    try:
        with source.open("r", encoding="utf-8") as stream:
            return json.load(stream)
    except UnicodeDecodeError as error:
        raise DataValidationError("input must be valid UTF-8") from error
    except json.JSONDecodeError as error:
        raise DataValidationError(
            f"invalid JSON at line {error.lineno}, column {error.colno}"
        ) from error
    except OSError as error:
        raise DataValidationError("input could not be read") from error


def read_json(paths: LabPaths, path: str | Path) -> Any:
    source = paths.resolve_input(path)
    return _read_json_file(source)


def read_generated_json(paths: LabPaths, path: str | Path) -> Any:
    source = paths.resolve_generated_input(path)
    return _read_json_file(source)


def _array_from_root(data: Any, key: str) -> list[Any]:
    if isinstance(data, list):
        return data
    if isinstance(data, dict) and isinstance(data.get(key), list):
        return data[key]
    raise DataValidationError(f"JSON root must be an array or contain a '{key}' array")


def load_documents(paths: LabPaths, path: str | Path) -> list[Document]:
    records = _array_from_root(read_json(paths, path), "documents")
    try:
        return [Document.from_dict(record) for record in records]
    except (AttributeError, TypeError, ValueError) as error:
        raise DataValidationError(f"invalid document record: {error}") from error


def load_evaluation_cases(
    paths: LabPaths, path: str | Path
) -> list[EvaluationCase]:
    records = _array_from_root(read_json(paths, path), "evaluation_cases")
    try:
        return [EvaluationCase.from_dict(record) for record in records]
    except (AttributeError, TypeError, ValueError) as error:
        raise DataValidationError(f"invalid evaluation case: {error}") from error


def write_json_report(
    paths: LabPaths, relative_path: str | Path, data: Any
) -> Path:
    target = paths.resolve_output(relative_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(f".{target.name}.tmp")
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as stream:
            json.dump(data, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        os.replace(temporary, target)
    except (OSError, TypeError, ValueError):
        temporary.unlink(missing_ok=True)
        raise
    return target


def write_text_report(
    paths: LabPaths, relative_path: str | Path, text: str
) -> Path:
    target = paths.resolve_output(relative_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(f".{target.name}.tmp")
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as stream:
            stream.write(text)
            if text and not text.endswith("\n"):
                stream.write("\n")
        os.replace(temporary, target)
    except OSError:
        temporary.unlink(missing_ok=True)
        raise
    return target
