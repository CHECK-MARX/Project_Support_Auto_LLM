import pytest

from rag_lab.normalization import NormalizationOptions, normalize_text


def test_normalizes_japanese_utf8_unicode_and_newlines() -> None:
    result = normalize_text("ＣＣＴを生成します。  \r\n\r\n\r\n完了です。\r\n")

    assert result.text == "CCTを生成します。\n\n完了です。"


def test_configured_boilerplate_is_separated_from_main_text() -> None:
    source = (
        "いつもお世話になっております。\n"
        "調査結果を報告します。\n"
        "----------\n"
        "オンラインセミナーのご案内です。\n"
        "署名:\n"
        "テスト担当\n"
    )
    options = NormalizationOptions(
        exclude_categories=frozenset(
            {"greeting", "separator", "webinar", "signature"}
        )
    )

    result = normalize_text(source, options)

    assert result.text == "調査結果を報告します。"
    assert {segment.category for segment in result.excluded_segments} == {
        "greeting",
        "separator",
        "webinar",
        "signature",
    }
    assert any("テスト担当" in item.text for item in result.excluded_segments)


def test_quoted_mail_block_can_be_excluded() -> None:
    result = normalize_text(
        "今回の回答です。\n-----Original Message-----\n過去の本文\n> 引用行",
        NormalizationOptions(exclude_categories=frozenset({"quoted_mail"})),
    )

    assert result.text == "今回の回答です。"
    assert len(result.excluded_segments) == 1
    assert "過去の本文" in result.excluded_segments[0].text


def test_source_string_is_not_mutated() -> None:
    source = "原文\r\n署名:\r\n担当者"
    original = source

    normalize_text(
        source, NormalizationOptions(exclude_categories=frozenset({"signature"}))
    )

    assert source == original


def test_unknown_exclusion_category_is_rejected() -> None:
    with pytest.raises(ValueError, match="unknown normalization categories"):
        NormalizationOptions(exclude_categories=frozenset({"unknown"}))
