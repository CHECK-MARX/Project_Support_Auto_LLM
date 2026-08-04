import pytest

from rag_lab.chunking import ChunkingOptions, ChunkingStrategy, chunk_document
from rag_lab.models import Document, DocumentMetadata


def test_fixed_chunks_have_configured_overlap_and_stable_ids() -> None:
    document = Document("doc-fixed", "abcdefghij")
    options = ChunkingOptions(
        strategy=ChunkingStrategy.FIXED,
        max_characters=4,
        overlap_characters=1,
    )

    first = chunk_document(document, options)
    second = chunk_document(document, options)

    assert [chunk.text for chunk in first] == ["abcd", "defg", "ghij"]
    assert [chunk.chunk_id for chunk in first] == [chunk.chunk_id for chunk in second]


def test_paragraph_chunking_keeps_paragraph_boundaries() -> None:
    document = Document("doc-paragraph", "第一段落。\n\n第二段落。")

    chunks = chunk_document(
        document,
        ChunkingOptions(
            strategy=ChunkingStrategy.PARAGRAPH,
            max_characters=100,
            overlap_characters=0,
        ),
    )

    assert [chunk.text for chunk in chunks] == ["第一段落。", "第二段落。"]


def test_heading_chunking_keeps_heading_and_section_title() -> None:
    document = Document(
        "doc-heading", "# 概要\n概要本文\n\n## 手順\n手順本文"
    )

    chunks = chunk_document(
        document,
        ChunkingOptions(
            strategy=ChunkingStrategy.HEADING,
            max_characters=100,
            overlap_characters=0,
        ),
    )

    assert [chunk.section_title for chunk in chunks] == ["概要", "手順"]
    assert chunks[0].text.startswith("# 概要")
    assert chunks[1].text.startswith("## 手順")


def test_structured_chunking_uses_explicit_metadata_only() -> None:
    metadata = DocumentMetadata(
        product_name="SyntheticProduct",
        support_id="SYN-1",
        question="質問内容",
        cause=None,
        resolution="対処内容",
        customer_final_reply="最終回答",
    )
    document = Document("doc-structured", "構造化されていない本文", metadata)

    chunks = chunk_document(
        document,
        ChunkingOptions(
            strategy=ChunkingStrategy.STRUCTURED,
            max_characters=100,
            overlap_characters=0,
        ),
    )

    assert [chunk.section_title for chunk in chunks] == [
        "質問",
        "対処",
        "お客様向け最終回答",
    ]
    assert all(chunk.metadata is metadata for chunk in chunks)
    assert not any(chunk.section_title == "原因" for chunk in chunks)


def test_structured_chunking_parses_explicit_headings_without_inference() -> None:
    document = Document(
        "doc-structured-text",
        "質問: 発生条件は何ですか。\n原因: 設定値が不足しています。\n対処: 設定値を追加します。",
    )

    chunks = chunk_document(
        document,
        ChunkingOptions(
            strategy="structured", max_characters=100, overlap_characters=0
        ),
    )

    assert [chunk.section_title for chunk in chunks] == ["質問", "原因", "対処"]
    assert "設定値が不足" in chunks[1].text


@pytest.mark.parametrize(
    ("maximum", "overlap"),
    [(0, 0), (10, -1), (10, 10), (10, 11)],
)
def test_invalid_chunk_sizes_are_rejected(maximum: int, overlap: int) -> None:
    with pytest.raises(ValueError):
        ChunkingOptions(max_characters=maximum, overlap_characters=overlap)
