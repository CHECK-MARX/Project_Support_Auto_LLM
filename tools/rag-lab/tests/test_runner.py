from __future__ import annotations

import json
from pathlib import Path

import pytest

from rag_lab.runner import run_evaluation, run_evidence


def _write_synthetic_lab(root: Path) -> Path:
    samples = root / "samples"
    samples.mkdir()
    (samples / "documents.json").write_text(
        json.dumps(
            {
                "documents": [
                    {
                        "document_id": "doc-1",
                        "text": "質問: Path Traversalの判定\n対処: Canonical Pathを確認する。",
                        "metadata": {
                            "product_name": "Checkmarx",
                            "support_id": "SYN-1",
                            "document_name": "synthetic.txt",
                            "source_path": r"C:\private\customer\synthetic.txt",
                            "target_version": "1.0",
                            "question": "Path Traversalの判定",
                            "resolution": "Canonical Pathを確認する。"
                        },
                    },
                    {
                        "document_id": "doc-2",
                        "text": "CCTの生成手順",
                        "metadata": {
                            "product_name": "HelixQAC",
                            "support_id": "SYN-2",
                            "document_name": "qac.txt",
                            "source_path": r"C:\private\customer\qac.txt",
                            "target_version": "2.0"
                        },
                    },
                ]
            },
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )
    (samples / "cases.json").write_text(
        json.dumps(
            {
                "evaluation_cases": [
                    {
                        "query_id": "q1",
                        "product": "Checkmarx",
                        "query": "Path Traversal Canonical Path",
                        "target_version": "1.0",
                        "expected_document_ids": ["doc-1"],
                        "expected_support_ids": ["SYN-1"],
                        "required_terms": ["Path Traversal"],
                        "excluded_terms": ["HelixQAC"],
                    }
                ]
            },
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )
    config = {
        "inputRoots": ["samples"],
        "outputRoot": "reports/generated",
        "documentsFile": "samples/documents.json",
        "evaluationCasesFile": "samples/cases.json",
        "reportName": "test-comparison",
        "normalization": {},
        "chunking": {"maxCharacters": 200, "overlapCharacters": 20},
        "evaluation": {
            "chunkStrategies": ["fixed", "structured"],
            "searchMethods": ["keyword", "bm25"],
            "filterModes": ["none", "product_and_version"],
            "topK": [1, 3],
        },
    }
    config_path = root / "test-config.json"
    config_path.write_text(
        json.dumps(config, ensure_ascii=False), encoding="utf-8"
    )
    return config_path


def test_runner_writes_three_redacted_comparison_reports(tmp_path: Path) -> None:
    config_path = _write_synthetic_lab(tmp_path)

    output = run_evaluation(tmp_path, config_path=config_path)

    assert len(output.report["summary"]) == 16
    assert set(output.files) == {"json", "csv", "markdown"}
    assert all(path.is_file() for path in output.files.values())
    json_text = output.files["json"].read_text(encoding="utf-8")
    assert "C:\\\\private" not in json_text
    assert "source_path" not in json_text
    assert "source_name" in json_text
    assert "mean_precision_at_k" in output.files["csv"].read_text(encoding="utf-8")
    assert "| chunk_strategy |" in output.files["markdown"].read_text(
        encoding="utf-8"
    )


def test_runner_rejects_report_name_path_traversal(tmp_path: Path) -> None:
    config_path = _write_synthetic_lab(tmp_path)

    with pytest.raises(ValueError, match="report name"):
        run_evaluation(
            tmp_path, config_path=config_path, report_name="../outside"
        )


def test_phase4_runner_compares_embedding_hybrid_and_reranking(tmp_path: Path) -> None:
    config_path = _write_synthetic_lab(tmp_path)
    config = json.loads(config_path.read_text(encoding="utf-8"))
    config["evaluation"] = {
        "chunkStrategies": ["heading"],
        "searchMethods": ["hash_embedding", "hybrid"],
        "rerankers": ["none", "lexical"],
        "filterModes": ["product_and_version"],
        "topK": [1],
    }
    config_path.write_text(
        json.dumps(config, ensure_ascii=False), encoding="utf-8"
    )

    output = run_evaluation(tmp_path, config_path=config_path)

    assert len(output.report["summary"]) == 4
    assert {row["reranker"] for row in output.report["summary"]} == {
        "none",
        "lexical",
    }
    assert {row["search_method"] for row in output.report["summary"]} == {
        "hash_embedding",
        "hybrid",
    }


def test_evidence_runner_writes_future_codex_shape_without_paths(tmp_path: Path) -> None:
    config_path = _write_synthetic_lab(tmp_path)

    output = run_evidence(
        tmp_path,
        query_id="q1",
        config_path=config_path,
        top_k=3,
    )

    assert output.file.is_file()
    assert set(output.payload) == {"query", "selectedEvidence"}
    assert output.payload["selectedEvidence"]
    text = output.file.read_text(encoding="utf-8")
    assert "source_path" not in text
    assert "C:\\\\private" not in text
