from rag_lab.models import Chunk, DocumentMetadata
from rag_lab.reranking import LexicalOverlapReranker, RerankerMethod, apply_reranker
from rag_lab.search import KeywordSearchIndex, SearchResult


def _result(document_id: str, text: str, score: float, rank: int) -> SearchResult:
    chunk = Chunk(
        chunk_id=f"{document_id}:0",
        document_id=document_id,
        chunk_index=0,
        strategy="test",
        text=text,
        metadata=DocumentMetadata(product_name="Product"),
    )
    return SearchResult(chunk, score, rank)


def test_lexical_reranker_can_promote_more_complete_match() -> None:
    reranker = LexicalOverlapReranker()
    candidates = [
        _result("base-first", "関係のない説明", 1.0, 1),
        _result("exact", "Path Traversal Not Exploitable", 0.9, 2),
    ]

    results = reranker.rerank(
        "Path Traversal Not Exploitable", candidates, top_k=2
    )

    assert results[0].chunk.document_id == "exact"
    assert results[0].rank == 1


def test_reranker_wrapper_preserves_none_and_supports_lexical() -> None:
    chunk = _result("doc", "CCT生成方法", 1.0, 1).chunk
    index = KeywordSearchIndex([chunk])

    assert apply_reranker(index, RerankerMethod.NONE) is index
    reranked = apply_reranker(index, RerankerMethod.LEXICAL)
    assert reranked.search("CCT生成", top_k=1)[0].chunk.document_id == "doc"
