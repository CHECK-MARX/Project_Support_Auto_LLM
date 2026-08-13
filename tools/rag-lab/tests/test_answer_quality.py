from __future__ import annotations

import json
from pathlib import Path

from rag_lab.answer_quality import (
    BLOCKED,
    CUSTOMER_READY,
    INSUFFICIENT_EVIDENCE,
    NEEDS_REVIEW,
    AnswerQualityEvidence,
    AnswerQualityInput,
    create_support_catalog,
    evaluate_answer_quality,
    extract_technical_claims,
)
from rag_lab.answer_quality_runner import run_answer_quality_comparison


CASES_FILE = Path(__file__).resolve().parents[1] / "samples" / "phase17_answer_quality_cases.json"


def test_all_synthetic_cases_match_expected_safe_decisions() -> None:
    cases = _cases()
    results = [_evaluate(case) for case in cases]

    assert [item["decision"] for item in results] == [
        case["expectedDecision"] for case in cases
    ]
    assert [item["unsupportedClaimCount"] for item in results] == [
        case["expectedUnsupportedClaimCount"] for case in cases
    ]
    false_positives = sum(
        result["decision"] == CUSTOMER_READY
        and case["expectedDecision"] != CUSTOMER_READY
        for case, result in zip(cases, results, strict=True)
    )
    false_negatives = sum(
        result["decision"] == BLOCKED
        and case["expectedDecision"] == CUSTOMER_READY
        for case, result in zip(cases, results, strict=True)
    )
    assert false_positives == 0
    assert false_negatives == 0


def test_directness_grounding_topic_coverage_and_actionability_are_independent() -> None:
    good = _evaluate(_cases()[0])
    wrong_topic = _evaluate(_case("F-wrong-topic-natural"))

    assert good["directness"] == 1.0
    assert good["grounding"] == 1.0
    assert good["topicAlignment"] == 1.0
    assert good["coverage"] == 1.0
    assert good["actionability"] == 1.0
    assert wrong_topic["topicAlignment"] == 0.0
    assert wrong_topic["decision"] == BLOCKED


def test_detects_unsupported_command_option_and_version() -> None:
    unsupported = _evaluate(_case("D-unsupported-command"))
    version = evaluate_answer_quality(
        AnswerQualityInput(
            question="Validate Stream Version 2025.4の設定方法",
            answer="Validate StreamをVersion 9.9で設定してください。",
            product_name="HelixQAC",
            evidence=(
                AnswerQualityEvidence(
                    "version",
                    "OfficialDoc",
                    "Validate Stream Version 2025.4の設定方法です。",
                ),
            ),
        )
    )

    assert {item["kind"] for item in unsupported["unsupportedTechnicalClaims"]} == {
        "Command", "Option"
    }
    assert version["unsupportedClaimCount"] == 1
    assert version["unsupportedTechnicalClaims"][0]["kind"] == "Version"
    assert version["decision"] == BLOCKED


def test_internal_leakage_and_version_conflict_require_review() -> None:
    leakage = _evaluate(_case("H-internal-leakage"))
    conflict = _evaluate(_case("E-version-conflict"))

    assert leakage["internalLeakageCount"] >= 1
    assert leakage["decision"] == NEEDS_REVIEW
    assert conflict["conflictCount"] == 1
    assert conflict["decision"] == NEEDS_REVIEW


def test_no_evidence_is_insufficient_and_natural_wrong_topic_is_blocked() -> None:
    assert _evaluate(_case("C-insufficient-evidence"))["decision"] == INSUFFICIENT_EVIDENCE
    assert _evaluate(_case("F-wrong-topic-natural"))["decision"] == BLOCKED


def test_technical_correctness_is_separate_from_customer_writing_quality() -> None:
    result = _evaluate(_case("G-correct-technical-awkward-style"))

    assert result["technicalFidelity"] == 1.0
    assert result["grounding"] == 1.0
    assert result["customerReadiness"] < 0.65
    assert result["decision"] == NEEDS_REVIEW


def test_claim_extraction_avoids_general_words_and_supports_json_serialization() -> None:
    catalog = create_support_catalog("HelixQAC")
    claims = extract_technical_claims(
        "通常の設定方法です。qacli validate build --project Demo、Version 2025.4、Port 8080、config.jsonを使います。",
        catalog,
    )
    assert {item.kind for item in claims}.issuperset(
        {"Command", "Option", "Version", "Port", "File"}
    )
    payload = _evaluate(_cases()[0])
    restored = json.loads(json.dumps(payload, ensure_ascii=False))
    assert restored["decision"] == CUSTOMER_READY


def _cases() -> list[dict[str, object]]:
    return json.loads(CASES_FILE.read_text(encoding="utf-8"))["cases"]


def test_phase17_comparison_writes_redacted_report_and_passes_gate(
    tmp_path: Path,
) -> None:
    samples = tmp_path / "samples"
    samples.mkdir()
    (samples / CASES_FILE.name).write_bytes(CASES_FILE.read_bytes())

    output = run_answer_quality_comparison(tmp_path)

    gate = output.report["qualityGate"]
    assert gate["status"] == "passed"
    assert gate["falsePositiveCount"] == 0
    assert gate["falseNegativeCount"] == 0
    assert gate["mismatchCount"] == 0
    assert set(output.files) == {"json", "markdown"}
    assert all(path.is_file() for path in output.files.values())
    report_text = output.files["json"].read_text(encoding="utf-8")
    assert '"answer"' not in report_text
    assert '"text"' not in report_text
    assert output.report["containsRawQuestionAnswerOrEvidence"] is False
    assert set(output.report["aggregate"]) == {
        "phase14", "phase15", "phase16", "phase17"
    }


def _case(case_id: str) -> dict[str, object]:
    return next(item for item in _cases() if item["id"] == case_id)


def _evaluate(case: dict[str, object]) -> dict[str, object]:
    answers = case["answers"]
    return evaluate_answer_quality(
        AnswerQualityInput(
            question=case["question"],
            answer=answers["phase16"],
            product_name=case["product"],
            evidence=tuple(
                AnswerQualityEvidence(
                    source_id=item["sourceId"],
                    source_type=item["sourceType"],
                    text=item["text"],
                    version=item.get("version"),
                )
                for item in case["evidence"]
            ),
        )
    )
