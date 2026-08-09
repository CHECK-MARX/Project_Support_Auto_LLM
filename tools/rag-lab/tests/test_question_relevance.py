from __future__ import annotations

from rag_lab.evidence import EvidenceSelectionOptions, build_codex_evidence
from rag_lab.models import Chunk, DocumentMetadata
from rag_lab.question_relevance import (
    CoverageItem,
    InsufficientReason,
    QuestionType,
    classify_question,
    comparison_report,
    extract_exact_technical_tokens,
    select_question_aware_evidence,
)
from rag_lab.search import SearchResult


QUERY = (
    "QACで解析した結果をValidateへアップロードするコマンドについて教えてください。"
    "コマンドについて、一連の手順について具体的に、アップロードが完了して"
    "Validateで確認できるところまで行いたいです。"
)


def _result(
    chunk_id: str,
    text: str,
    *,
    score: float,
    source_type: str = "Manual",
    product: str = "HelixQAC",
    version: str | None = None,
    document_id: str | None = None,
) -> SearchResult:
    chunk = Chunk(
        chunk_id=chunk_id,
        document_id=document_id or chunk_id,
        chunk_index=0,
        strategy="synthetic",
        text=text,
        metadata=DocumentMetadata(
            product_name=product,
            target_version=version,
            source_type=source_type,
            document_name=f"{chunk_id}.txt",
        ),
    )
    return SearchResult(chunk=chunk, score=score, rank=1, matched_terms=())


def _representative_results() -> list[SearchResult]:
    return [
        _result(
            "surrounding-official",
            "Validate製品の概要とダッシュボードの説明です。",
            score=0.99,
            source_type="OfficialDoc",
        ),
        _result(
            "command",
            "```qacli validate upload --qaf-project Demo --build-name Build01``` を実行します。",
            score=0.55,
        ),
        _result(
            "auth-project",
            "1. Validateへログインして認証tokenを取得します。\n2. projectを関連付けて接続します。",
            score=0.52,
        ),
        _result(
            "verify",
            "3. コマンドを実行します。完了後、Validate portalのBuild画面で結果を確認します。失敗時はerrorログを確認します。",
            score=0.50,
        ),
    ]


def test_question_type_detection_for_representative_query() -> None:
    profile = classify_question(QUERY)
    assert QuestionType.COMMAND in profile.types
    assert QuestionType.HOW_TO in profile.types
    assert profile.upload_workflow is True


def test_command_question_type_detection() -> None:
    assert QuestionType.COMMAND in classify_question("qacliのコマンドを教えて").types


def test_how_to_question_type_detection() -> None:
    assert QuestionType.HOW_TO in classify_question("設定方法と手順を教えて").types


def test_configuration_question_type_detection() -> None:
    assert QuestionType.CONFIGURATION in classify_question("接続設定を確認したい").types


def test_troubleshooting_question_type_detection() -> None:
    assert QuestionType.TROUBLESHOOTING in classify_question("timeoutの原因と対処を教えて").types


def test_version_question_type_detection() -> None:
    profile = classify_question("version 2025.4の手順")
    assert QuestionType.VERSION in profile.types
    assert profile.requested_version == "2025.4"


def test_permission_question_type_detection() -> None:
    assert QuestionType.PERMISSION in classify_question("権限不足を解消したい").types


def test_error_message_question_type_detection() -> None:
    assert QuestionType.ERROR_MESSAGE in classify_question("エラーコードの意味は何ですか").types


def test_exact_technical_token_extraction_does_not_infer_missing_options() -> None:
    tokens = extract_exact_technical_tokens("qacli upload --qaf-project Demo")
    assert "qacli" in [item.casefold() for item in tokens]
    assert "--qaf-project" in tokens
    assert "--build-name" not in tokens


def test_command_example_is_ranked_above_surrounding_official_information() -> None:
    selection = select_question_aware_evidence(
        QUERY, _representative_results(), product="HelixQAC", top_k=3
    )
    assert selection.selected[0].result.chunk.chunk_id == "command"
    assert "surrounding-official" not in [
        item.result.chunk.chunk_id for item in selection.selected
    ]


def test_procedure_coverage_selection_uses_complementary_chunks() -> None:
    selection = select_question_aware_evidence(
        QUERY, _representative_results(), product="HelixQAC", top_k=3
    )
    assert CoverageItem.UPLOAD_COMMAND in selection.final_coverage
    assert CoverageItem.AUTHENTICATION in selection.final_coverage
    assert CoverageItem.PROJECT_ASSOCIATION in selection.final_coverage
    assert CoverageItem.VALIDATE_VERIFICATION in selection.final_coverage


def test_same_document_complementary_chunks_are_not_dropped() -> None:
    results = [
        _result("same-1", "qacli upload --qaf-project Demo", score=0.8, document_id="same"),
        _result("same-2", "Validateへログインし、完了後にportalで確認します。", score=0.7, document_id="same"),
    ]
    selection = select_question_aware_evidence(QUERY, results, product="HelixQAC", top_k=2)
    assert len(selection.selected) == 2


def test_duplicate_text_is_removed() -> None:
    results = [
        _result("a", "qacli upload --qaf-project Demo", score=0.8),
        _result("b", "qacli upload --qaf-project Demo", score=0.7),
    ]
    selection = select_question_aware_evidence(QUERY, results, product="HelixQAC", top_k=5)
    assert len(selection.selected) == 1


def test_missing_command_reports_insufficient_reason() -> None:
    result = _result("overview", "Validateの概要です。", score=0.9)
    selection = select_question_aware_evidence(QUERY, [result], product="HelixQAC")
    assert InsufficientReason.MISSING_COMMAND in selection.insufficient_reasons


def test_product_mismatch_is_excluded() -> None:
    result = _result("wrong", "qacli upload --qaf-project Demo", score=1.0, product="Checkmarx")
    selection = select_question_aware_evidence(QUERY, [result], product="HelixQAC")
    assert not selection.selected
    assert InsufficientReason.PRODUCT_MISMATCH in selection.insufficient_reasons


def test_version_not_requested_does_not_require_version_evidence() -> None:
    selection = select_question_aware_evidence(
        QUERY, [_result("command", "qacli upload --qaf-project Demo", score=0.8)], product="HelixQAC"
    )
    assert InsufficientReason.MISSING_VERSION not in selection.insufficient_reasons


def test_version_mismatch_is_reported_when_query_requests_version() -> None:
    query = QUERY + " 対象は2025.4です。"
    selection = select_question_aware_evidence(
        query,
        [_result("old", "qacli upload --qaf-project Demo", score=0.8, version="2024.1")],
        product="HelixQAC",
    )
    assert selection.selected[0].version_match == "mismatch"
    assert InsufficientReason.MISSING_VERSION in selection.insufficient_reasons


def test_question_aware_output_adds_diagnostics_without_changing_legacy_default() -> None:
    results = _representative_results()
    legacy = build_codex_evidence(QUERY, results, options=EvidenceSelectionOptions(top_k=3))
    aware = build_codex_evidence(
        QUERY,
        results,
        product="HelixQAC",
        options=EvidenceSelectionOptions(top_k=3),
        question_aware=True,
    )
    assert set(legacy) == {"query", "selectedEvidence"}
    assert "questionAwareDiagnostics" in aware
    assert len(aware["selectedEvidence"]) <= 3


def test_ab_comparison_contains_safe_phase14_and_phase15_metrics() -> None:
    results = _representative_results()
    selection = select_question_aware_evidence(QUERY, results, product="HelixQAC", top_k=3)
    report = comparison_report(QUERY, results, selection, top_k=3)
    assert report["dataClassification"] == "synthetic"
    assert report["phase14"]["topIds"]
    assert report["phase15"]["finalCoverage"]
    assert "text" not in str(report).casefold()
