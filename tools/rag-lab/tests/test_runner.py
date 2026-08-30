from __future__ import annotations

import json
from pathlib import Path

import pytest

from rag_lab.runner import (
    _fingerprint,
    run_evaluation,
    run_evidence,
    run_topic_ranking_comparison,
)


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
    assert output.report["quality_gate"]["status"] in {"passed", "failed"}
    assert len(output.report["input_fingerprints"]) == 2
    assert all(
        set(item) == {"file_name", "size_bytes", "sha256"}
        for item in output.report["input_fingerprints"]
    )
    assert all(
        len(item["sha256"]) == 64 for item in output.report["input_fingerprints"]
    )
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


def test_input_fingerprint_is_independent_of_line_ending_style(
    tmp_path: Path,
) -> None:
    source = tmp_path / "synthetic.json"
    source.write_bytes(b'{\n  "value": 1\n}\n')
    lf_fingerprint = _fingerprint(source)
    source.write_bytes(b'{\r\n  "value": 1\r\n}\r\n')

    assert _fingerprint(source) == lf_fingerprint


def test_phase16_runner_writes_safe_abc_report_with_stream_gate() -> None:
    lab_root = Path(__file__).resolve().parents[1]

    output = run_topic_ranking_comparison(
        lab_root,
        config_path="config.phase16.json",
        query_id="phase16-stream-overview-setup",
        top_k=3,
        output_name="test-phase16-topic-ranking-abc",
    )

    assert set(("phase14", "phase15", "phase16")).issubset(output.report)
    assert output.report["qualityGate"]["status"] == "passed"
    condition = output.report["qualityConditions"][0]
    assert condition["name"] == "StreamEvidenceRanksAboveLicenseEvidence"
    assert condition["passed"] is True
    assert output.report["phase16"]["topIds"][0].startswith("phase16-stream")
    assert "document_name" not in str(output.report)
    assert output.file.is_file()


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


def test_phase22_partition_is_deterministic_and_reports_stable_locator(
    tmp_path: Path,
) -> None:
    config_path = _write_synthetic_lab(tmp_path)
    config = json.loads(config_path.read_text(encoding="utf-8"))
    cases_path = tmp_path / "samples" / "cases.json"
    cases = json.loads(cases_path.read_text(encoding="utf-8"))
    cases["evaluation_cases"][0]["query_id"] = "p22-31"
    cases_path.write_text(json.dumps(cases, ensure_ascii=False), encoding="utf-8")
    config["evaluation"]["casePartition"] = "holdout"
    config_path.write_text(json.dumps(config, ensure_ascii=False), encoding="utf-8")

    output = run_evaluation(tmp_path, config_path=config_path)

    assert output.report["configuration"]["case_partition"] == "holdout"
    assert output.report["configuration"]["case_count"] == 1
    locator = output.report["details"][0]["queries"][0]["expected_evidence_locators"][0]
    assert locator["document_title"] == "synthetic.txt"
    assert locator["source_type"] is None
    assert len(locator["content_hash"]) == 64


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
