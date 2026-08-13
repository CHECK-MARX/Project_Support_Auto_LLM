from rag_lab.answer_quality import (
    AnswerQualityEvidence,
    AnswerQualityInput,
    CUSTOMER_READY,
    INSUFFICIENT_EVIDENCE,
    NEEDS_REVIEW,
    create_support_catalog,
    evaluate_answer_quality,
)
from rag_lab.phase175 import analyze_negation_aware_topics
from rag_lab.topic_entity_ranking import (
    TopicEntityRankingCandidate,
    TopicEntityRankingRequest,
    extract_topic_entity_profile,
    rank_topic_entity_candidates,
)


CATALOG = create_support_catalog("HelixQAC")
QUESTION = "Perforce QACの解析結果をValidateへアップロードする一連のCLI手順、オプション、認証、接続、確認、失敗時の確認事項を教えてください。"
FULL = """qacli authで認証します。qacli validate connect --url https://validate.example で接続します。
qacli validate config --project Demoで設定し、qacli validate build --qaf-project . --build-name Build01でアップロードします。
Validate portalのBuild一覧で表示を確認します。失敗時はエラーログと認証、接続設定を確認します。"""


def test_license_instead_of_stream_is_structured():
    result = analyze_negation_aware_topics("LicenseではなくStreamについて", CATALOG)
    assert "Stream" in result.primary_profile.features
    assert "License" in result.excluded_profile.features


def test_license_server_and_ide_plugin_are_excluded():
    result = analyze_negation_aware_topics(
        "License ServerやIDE PluginではなくValidateのStream機能について", CATALOG
    )
    assert "Validate" in result.primary_profile.products
    assert "Stream" in result.primary_profile.features
    assert {"License", "IDE Plugin"}.issubset(result.excluded_profile.features)
    assert any(item.value == "License Server" for item in result.excluded_profile.entities)


def test_stream_instead_of_license_server_reverses_profiles():
    result = analyze_negation_aware_topics("StreamではなくLicense Serverを確認したい", CATALOG)
    assert "Stream" in result.excluded_profile.features
    assert "License" in result.primary_profile.features


def test_no_negation_matches_phase16_profile():
    query = "Validate Streamの概要、作成、QACとの関連付け、設定後の確認"
    result = analyze_negation_aware_topics(query, CATALOG)
    assert result.primary_profile == extract_topic_entity_profile(query, CATALOG)
    assert not result.excluded_text_segments


def test_unknown_topics_are_not_guessed():
    result = analyze_negation_aware_topics("未知製品Alphaではなく未知機能Betaのみ", CATALOG)
    assert not result.primary_profile.products
    assert not result.primary_profile.features
    assert not result.excluded_profile.products
    assert not result.excluded_profile.features


def test_excluded_high_score_cannot_beat_primary_topic():
    request = _ranking_request(manual_license=False, max_items=1)
    result = rank_topic_entity_candidates(request)
    assert [item.candidate_id for item in result.selected] == ["stream"]


def test_fallback_preserves_explicit_manual_selection():
    result = rank_topic_entity_candidates(_ranking_request(manual_license=True, max_items=2))
    assert "license" in {item.candidate_id for item in result.selected}


def test_full_evidence_incomplete_answer_is_needs_review():
    result = _quality("qacli validate build --qaf-project . --build-name Build01でアップロードします。", FULL)
    assert result["evidenceCoverage"] == 1.0
    assert result["answerCoverage"] < 1.0
    assert "Authentication" in result["missingAnswerCoverage"]
    assert result["decision"] == NEEDS_REVIEW


def test_missing_evidence_is_insufficient():
    partial = "qacli validate build --qaf-project .でアップロードします。"
    result = _quality(partial, partial)
    assert result["evidenceCoverage"] < 1.0
    assert result["decision"] == INSUFFICIENT_EVIDENCE


def test_full_evidence_and_answer_can_be_customer_ready():
    result = _quality(FULL, FULL)
    assert result["evidenceCoverage"] == 1.0
    assert result["answerCoverage"] == 1.0
    assert result["decision"] == CUSTOMER_READY


def _ranking_request(*, manual_license: bool, max_items: int) -> TopicEntityRankingRequest:
    query = "License ServerやIDE PluginではなくValidate Streamについて"
    analysis = analyze_negation_aware_topics(query, CATALOG)
    candidates = (
        _candidate(0, "license", "Validate License Serverの設定方法", 1.0, manual_license),
        _candidate(1, "stream", "Validate Streamの概要、用途、作成、QACとの関連付け、設定、確認方法", 0.3),
        _candidate(2, "ide", "Validate IDE Pluginの設定方法", 0.95),
    )
    return TopicEntityRankingRequest(
        query_profile=analysis.primary_profile,
        excluded_profile=analysis.excluded_profile,
        candidates=candidates,
        requested_product="HelixQAC",
        max_items=max_items,
    )


def _candidate(index: int, identifier: str, text: str, score: float, manual: bool = False):
    return TopicEntityRankingCandidate(
        candidate_index=index,
        candidate_id=identifier,
        text=text,
        source_type="Manual",
        product_name="HelixQAC",
        version=None,
        base_search_score=score,
        original_rank=index + 1,
        profile=extract_topic_entity_profile(text, CATALOG),
        manually_selected=manual,
    )


def _quality(answer: str, evidence: str):
    return evaluate_answer_quality(AnswerQualityInput(
        question=QUESTION,
        answer=answer,
        product_name="HelixQAC",
        evidence=(AnswerQualityEvidence("synthetic", "Manual", evidence, "HelixQAC"),),
        catalog=CATALOG,
        use_separated_coverage=True,
    ))
