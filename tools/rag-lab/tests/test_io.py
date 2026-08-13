from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from rag_lab.io import (
    DataValidationError,
    LabPaths,
    PathValidationError,
    load_documents,
    load_evaluation_cases,
    read_json,
    write_json_report,
    write_text_report,
)


def _lab(tmp_path: Path) -> tuple[LabPaths, Path]:
    samples = tmp_path / "samples"
    samples.mkdir()
    return LabPaths.create(tmp_path), samples


def test_loads_japanese_utf8_documents_and_preserves_source_file(tmp_path: Path) -> None:
    paths, samples = _lab(tmp_path)
    source = samples / "documents.json"
    source.write_text(
        json.dumps(
            {
                "documents": [
                    {
                        "document_id": "doc-ja",
                        "text": "日本語の問い合わせです。",
                        "metadata": {"product_name": "人工製品"},
                    }
                ]
            },
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )
    before = hashlib.sha256(source.read_bytes()).digest()

    documents = load_documents(paths, "samples/documents.json")

    after = hashlib.sha256(source.read_bytes()).digest()
    assert documents[0].text == "日本語の問い合わせです。"
    assert documents[0].metadata.product_name == "人工製品"
    assert before == after


def test_loads_synthetic_evaluation_cases(tmp_path: Path) -> None:
    paths, samples = _lab(tmp_path)
    source = samples / "cases.json"
    source.write_text(
        json.dumps(
            [
                {
                    "query_id": "q1",
                    "product": "人工製品",
                    "query": "確認方法",
                    "expected_document_ids": ["doc-1"],
                    "expected_support_ids": [],
                    "required_terms": ["確認"],
                    "excluded_terms": [],
                }
            ],
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )

    cases = load_evaluation_cases(paths, source)

    assert cases[0].query_id == "q1"
    assert cases[0].expected_document_ids == ("doc-1",)


def test_invalid_json_is_reported_as_data_error(tmp_path: Path) -> None:
    paths, samples = _lab(tmp_path)
    (samples / "invalid.json").write_text("{invalid", encoding="utf-8")

    with pytest.raises(DataValidationError, match="invalid JSON"):
        read_json(paths, "samples/invalid.json")


def test_non_utf8_input_is_rejected(tmp_path: Path) -> None:
    paths, samples = _lab(tmp_path)
    (samples / "shift-jis.json").write_bytes("日本語".encode("shift_jis"))

    with pytest.raises(DataValidationError, match="UTF-8"):
        read_json(paths, "samples/shift-jis.json")


def test_input_path_outside_allowed_roots_is_rejected(tmp_path: Path) -> None:
    paths, _ = _lab(tmp_path)
    outside = tmp_path / "outside.json"
    outside.write_text("{}", encoding="utf-8")

    with pytest.raises(PathValidationError, match="outside"):
        read_json(paths, outside)


def test_output_is_limited_to_generated_report_root(tmp_path: Path) -> None:
    paths, _ = _lab(tmp_path)

    target = write_json_report(paths, "phase2/result.json", {"status": "ok"})

    assert target == tmp_path / "reports" / "generated" / "phase2" / "result.json"
    assert json.loads(target.read_text(encoding="utf-8")) == {"status": "ok"}

    with pytest.raises(PathValidationError, match="outside"):
        write_json_report(paths, "../../outside.json", {})

    text_target = write_text_report(paths, "phase2/result.md", "# 結果")
    assert text_target.read_text(encoding="utf-8") == "# 結果\n"


def test_output_root_cannot_be_configured_outside_lab(tmp_path: Path) -> None:
    with pytest.raises(PathValidationError, match="reports/generated"):
        LabPaths.create(tmp_path, output_root=tmp_path.parent / "reports")

    with pytest.raises(PathValidationError, match="reports/generated"):
        LabPaths.create(tmp_path, output_root="models")
