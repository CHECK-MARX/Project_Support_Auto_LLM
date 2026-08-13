from __future__ import annotations

from rag_lab.topic_entity_ranking import (
    AliasDefinition,
    EntityAliasDefinition,
    EntityKind,
    TopicCoverage,
    TopicEntityCatalog,
    TopicEntityRankingCandidate,
    TopicEntityRankingRequest,
    build_topic_entity_comparison_section,
    compare_topic_profiles,
    extract_topic_entity_profile,
    rank_topic_entity_candidates,
)


CATALOG = TopicEntityCatalog(
    products=(AliasDefinition("Perforce QAC", ("Helix QAC", "QAC")),),
    components=(AliasDefinition("Validate", ("Perforce Validate",)),),
    features=(
        AliasDefinition("Stream", ("ストリーム",)),
        AliasDefinition("License", ("ライセンス",)),
        AliasDefinition("IDE Plugin", ("IDEプラグイン", "IDE Plugin")),
        AliasDefinition("Build upload", ("validate build",)),
    ),
    objects=(AliasDefinition("Analysis result", ("解析結果",)),),
    entities=(
        EntityAliasDefinition(
            EntityKind.COMMAND,
            "qacli validate build",
            ("validate build",),
        ),
    ),
)


def test_extracts_topic_operation_and_intent_from_explicit_text() -> None:
    profile = extract_topic_entity_profile(
        "Perforce QACのValidateのストリーム機能について、機能概要と設定方法を教えてください。",
        CATALOG,
    )

    assert profile.products == ("Perforce QAC",)
    assert profile.components == ("Validate",)
    assert profile.features == ("Stream",)
    assert "Configuration" in profile.operations
    assert "Overview" in profile.intents
    assert "HowTo" in profile.intents


def test_extracts_command_upload_object_and_exact_entities() -> None:
    profile = extract_topic_entity_profile(
        "QACで解析結果をValidateへアップロードするには qacli validate build --project Demo を実行します。",
        CATALOG,
    )

    assert profile.products == ("Perforce QAC",)
    assert profile.components == ("Validate",)
    assert profile.features == ("Build upload",)
    assert profile.objects == ("Analysis result",)
    assert "Upload" in profile.operations
    assert any(
        item.kind is EntityKind.COMMAND and item.normalized_value == "validate build"
        for item in profile.entities
    )
    assert any(
        item.kind is EntityKind.OPTION and item.value == "--project"
        for item in profile.entities
    )


def test_does_not_invent_unspecified_topic_fields() -> None:
    profile = extract_topic_entity_profile("設定方法を教えてください。", CATALOG)

    assert profile.products == ()
    assert profile.components == ()
    assert profile.features == ()
    assert profile.objects == ()
    assert "Configuration" in profile.operations


def test_normalizes_case_width_and_command_prefix() -> None:
    profile = extract_topic_entity_profile(
        "ＶＡＬＩＤＡＴＥ STREAM and validate build", CATALOG
    )

    assert profile.components == ("Validate",)
    assert profile.features == ("Stream", "Build upload")
    assert any(
        item.kind is EntityKind.COMMAND and item.normalized_value == "validate build"
        for item in profile.entities
    )


def test_extracts_supported_entity_kinds_without_treating_acronym_as_setting() -> None:
    profile = extract_topic_entity_profile(
        "QAC 2025.4 on Windows uses Validate API, STREAM_MODE=enabled, VAL-1234, config.json and license server.",
        CATALOG,
    )

    assert any(item.kind is EntityKind.API and item.value == "Validate API" for item in profile.entities)
    assert any(item.kind is EntityKind.SETTING and item.value == "STREAM_MODE=enabled" for item in profile.entities)
    assert any(item.kind is EntityKind.ERROR_CODE and item.value == "VAL-1234" for item in profile.entities)
    assert any(item.kind is EntityKind.FILE and item.value == "config.json" for item in profile.entities)
    assert any(item.kind is EntityKind.VERSION and item.value == "2025.4" for item in profile.entities)
    assert any(item.kind is EntityKind.OPERATING_SYSTEM and item.value == "Windows" for item in profile.entities)
    assert any(item.kind is EntityKind.SERVER_TYPE and item.value == "License Server" for item in profile.entities)
    assert not any(item.kind is EntityKind.SETTING and item.value == "QAC" for item in profile.entities)


def test_detects_stream_versus_license_conflict() -> None:
    query = extract_topic_entity_profile("Validate Streamの設定方法", CATALOG)
    evidence = extract_topic_entity_profile(
        "Validate License server configuration", CATALOG
    )

    assessment = compare_topic_profiles(query, evidence)

    assert assessment.topic_conflict is True
    assert assessment.no_topic_match is True
    assert "Feature" in assessment.conflict_kinds
    assert assessment.matched_features == ()


def test_reports_product_and_topic_matches_for_same_topic() -> None:
    query = extract_topic_entity_profile("QAC Validate Stream setup", CATALOG)
    evidence = extract_topic_entity_profile(
        "Perforce QAC Validate Stream configuration", CATALOG
    )

    assessment = compare_topic_profiles(query, evidence)

    assert assessment.topic_conflict is False
    assert assessment.has_topic_match is True
    assert assessment.matched_products == ("Perforce QAC",)
    assert assessment.matched_components == ("Validate",)
    assert assessment.matched_features == ("Stream",)


def test_detects_stream_versus_ide_plugin_conflict() -> None:
    query = extract_topic_entity_profile("Validate Stream overview", CATALOG)
    evidence = extract_topic_entity_profile("Validate IDE Plugin setup", CATALOG)

    assessment = compare_topic_profiles(query, evidence)

    assert assessment.topic_conflict is True
    assert "Feature" in assessment.conflict_kinds


def test_unknown_evidence_topic_is_not_an_explicit_conflict() -> None:
    query = extract_topic_entity_profile("Validate Stream overview", CATALOG)
    evidence = extract_topic_entity_profile("General configuration guidance", CATALOG)

    assessment = compare_topic_profiles(query, evidence)

    assert assessment.topic_conflict is False
    assert assessment.no_topic_match is True


def test_matches_equivalent_command_entities() -> None:
    query = extract_topic_entity_profile("qacli validate buildの方法", CATALOG)
    evidence = extract_topic_entity_profile("validate build command reference", CATALOG)

    assessment = compare_topic_profiles(query, evidence)

    assert any(
        item.kind is EntityKind.COMMAND and item.normalized_value == "validate build"
        for item in assessment.matched_entities
    )


def test_ranking_prefers_stream_over_higher_base_license_evidence() -> None:
    result = _rank(
        _candidate(0, "license", "Validate License configuration and setup", 0.99),
        _candidate(1, "stream", "Validate Stream configuration and setup", 0.40),
    )

    assert result.selected[0].candidate_id == "stream"
    license_item = next(item for item in result.assessed if item.candidate_id == "license")
    assert license_item.topic_conflict is True
    assert license_item.conflict_penalty == -0.55


def test_ranking_uses_complementary_topic_coverage_for_top_k() -> None:
    result = _rank(
        _candidate(0, "overview", "Validate Stream overview: a Stream is used for organizing analysis data.", 0.70),
        _candidate(1, "duplicate", "Validate Stream overview and purpose.", 0.69),
        _candidate(2, "setup", "Validate Stream setup steps: create and configure the Stream, then associate the QAC project.", 0.62),
        _candidate(3, "verify", "Validate Stream verification: confirm its status after setup.", 0.58),
    )

    assert len(result.selected) == 3
    assert any(item.candidate_id == "setup" for item in result.selected)
    assert any(item.candidate_id == "verify" for item in result.selected)
    assert TopicCoverage.OVERVIEW in result.final_coverage
    assert TopicCoverage.SETUP in result.final_coverage
    assert TopicCoverage.VERIFICATION in result.final_coverage


def test_ranking_reports_missing_procedure_and_verification_separately() -> None:
    result = _rank(
        _candidate(0, "overview", "Validate Stream overview and purpose.", 0.8)
    )

    assert "MissingSetupProcedure" in result.insufficient_reasons
    assert "MissingVerification" in result.insufficient_reasons
    assert "LowCoverage" in result.insufficient_reasons


def test_ranking_reports_conflict_when_only_wrong_feature_exists() -> None:
    result = _rank(
        _candidate(0, "plugin", "Validate IDE Plugin setup and verification", 1.0)
    )

    assert result.selected == ()
    assert "NoTopicMatch" in result.insufficient_reasons
    assert "TopicConflict" in result.insufficient_reasons


def test_ranking_keeps_phase15_upload_coverage_for_complementary_chunks() -> None:
    upload_catalog = TopicEntityCatalog(
        products=CATALOG.products,
        components=CATALOG.components,
        features=(*CATALOG.features, AliasDefinition("Build upload", ("validate build", "build upload"))),
        objects=CATALOG.objects,
        entities=CATALOG.entities,
    )
    query = "QACの解析結果をValidateへアップロードするqacli validate buildの方法"
    texts = (
        "Run qacli validate build --project Demo to upload the analysis result.",
        "Authenticate with a token and associate the Validate project.",
        "After execution, verify the uploaded build in the Validate portal.",
    )
    candidates = tuple(
        TopicEntityRankingCandidate(
            candidate_index=index,
            candidate_id=f"upload-{index}",
            text=text,
            source_type="Manual",
            product_name="HelixQAC",
            version=None,
            base_search_score=0.7 - index * 0.05,
            original_rank=index + 1,
            profile=extract_topic_entity_profile(text, upload_catalog),
        )
        for index, text in enumerate(texts)
    )

    result = rank_topic_entity_candidates(
        TopicEntityRankingRequest(
            query_profile=extract_topic_entity_profile(query, upload_catalog),
            requested_product="HelixQAC",
            candidates=candidates,
            max_items=3,
        )
    )

    assert TopicCoverage.UPLOAD_COMMAND in result.final_coverage
    assert TopicCoverage.AUTHENTICATION in result.final_coverage
    assert TopicCoverage.PROJECT_ASSOCIATION in result.final_coverage
    assert TopicCoverage.VALIDATE_VERIFICATION in result.final_coverage


def test_phase16_comparison_is_redacted_and_stream_condition_passes() -> None:
    request = TopicEntityRankingRequest(
        query_profile=extract_topic_entity_profile(
            "Validate Streamの機能概要と設定方法", CATALOG
        ),
        requested_product="HelixQAC",
        candidates=(
            _candidate(0, "license", "Validate License configuration and setup", 0.99),
            _candidate(1, "stream", "Validate Stream overview, purpose, setup and verification", 0.40),
        ),
        max_items=3,
    )
    result = rank_topic_entity_candidates(request)

    section, condition = build_topic_entity_comparison_section(
        request, result, elapsed_ms=1.25
    )

    assert section["topIds"][0] == "stream"
    assert section["items"][0]["topicScore"] > 0
    assert condition["passed"] is True
    assert condition["streamTopRank"] == 1
    assert condition["licenseTopRank"] is None
    assert "Validate License configuration" not in str(section)


def _rank(*candidates: TopicEntityRankingCandidate):
    query = "Validate Streamの機能概要と設定方法を教えてください。"
    return rank_topic_entity_candidates(
        TopicEntityRankingRequest(
            query_profile=extract_topic_entity_profile(query, CATALOG),
            requested_product="HelixQAC",
            candidates=tuple(candidates),
            max_items=3,
        )
    )


def _candidate(
    index: int, candidate_id: str, text: str, score: float
) -> TopicEntityRankingCandidate:
    return TopicEntityRankingCandidate(
        candidate_index=index,
        candidate_id=candidate_id,
        text=text,
        source_type="Manual",
        product_name="HelixQAC",
        version=None,
        base_search_score=score,
        original_rank=index + 1,
        profile=extract_topic_entity_profile(text, CATALOG),
    )
