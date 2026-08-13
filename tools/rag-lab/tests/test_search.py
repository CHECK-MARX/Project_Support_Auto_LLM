from rag_lab.models import Chunk, DocumentMetadata
from rag_lab.search import (
    BM25SearchIndex,
    KeywordSearchIndex,
    SearchFilters,
    tokenize,
)


def _chunk(
    document_id: str,
    text: str,
    *,
    product: str,
    version: str,
    index: int = 0,
) -> Chunk:
    return Chunk(
        chunk_id=f"{document_id}:{index}",
        document_id=document_id,
        chunk_index=index,
        strategy="test",
        text=text,
        metadata=DocumentMetadata(
            product_name=product,
            target_version=version,
            document_name=f"{document_id}.txt",
        ),
    )


def test_tokenizer_generates_japanese_ngrams() -> None:
    tokens = tokenize("ＣＣＴの生成方法")

    assert "cct" in tokens
    assert "生成" in tokens
    assert "方法" in tokens


def test_keyword_search_ranks_more_complete_match_first() -> None:
    index = KeywordSearchIndex(
        [
            _chunk(
                "partial",
                "Path Traversalを確認します。",
                product="Checkmarx",
                version="1.0",
            ),
            _chunk(
                "complete",
                "Path TraversalをNot Exploitableと判定します。",
                product="Checkmarx",
                version="1.0",
            ),
        ]
    )

    results = index.search("Path Traversal Not Exploitable", top_k=2)

    assert [item.chunk.document_id for item in results] == ["complete", "partial"]
    assert results[0].score > results[1].score


def test_bm25_search_ranks_repeated_relevant_terms_first() -> None:
    index = BM25SearchIndex(
        [
            _chunk(
                "relevant",
                "CCT生成手順。CCT生成時は設定を確認します。",
                product="HelixQAC",
                version="2.0",
            ),
            _chunk(
                "other",
                "CCTという用語だけを記載します。",
                product="HelixQAC",
                version="2.0",
            ),
        ]
    )

    results = index.search("CCT 生成", top_k=2)

    assert results[0].chunk.document_id == "relevant"


def test_product_filter_prevents_cross_product_results() -> None:
    index = KeywordSearchIndex(
        [
            _chunk(
                "qac",
                "共通エラーの対処方法",
                product="HelixQAC",
                version="1.0",
            ),
            _chunk(
                "checkmarx",
                "共通エラーの対処方法",
                product="Checkmarx",
                version="1.0",
            ),
        ]
    )

    results = index.search(
        "共通エラー",
        top_k=5,
        filters=SearchFilters(product_name="Checkmarx"),
    )

    assert [item.chunk.document_id for item in results] == ["checkmarx"]


def test_version_filter_requires_exact_normalized_match() -> None:
    index = KeywordSearchIndex(
        [
            _chunk(
                "old", "設定手順", product="Product", version="1.0"
            ),
            _chunk(
                "current", "設定手順", product="Product", version="２．０"
            ),
        ]
    )

    results = index.search(
        "設定手順",
        top_k=5,
        filters=SearchFilters(product_name="product", target_version="2.0"),
    )

    assert [item.chunk.document_id for item in results] == ["current"]
