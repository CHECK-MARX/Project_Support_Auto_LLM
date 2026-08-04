from rag_lab.models import Document, DocumentMetadata, EvaluationCase


def test_metadata_preserves_fields_without_inference() -> None:
    metadata = DocumentMetadata.from_dict(
        {
            "productId": "product-1",
            "productName": "SyntheticProduct",
            "supportId": "SYN-1000",
            "sourcePath": r"C:\private\case\note.txt",
            "targetVersion": "1.2",
            "question": "確認事項",
        }
    )

    assert metadata.product_id == "product-1"
    assert metadata.target_version == "1.2"
    assert metadata.question == "確認事項"
    assert metadata.cause is None
    assert metadata.customer_final_reply is None


def test_public_metadata_does_not_expose_absolute_source_path() -> None:
    metadata = DocumentMetadata(source_path=r"C:\private\case\note.txt")

    public = metadata.to_dict()

    assert "source_path" not in public
    assert public["source_name"] == "note.txt"
    assert "private" not in str(public)


def test_document_accepts_camel_case_input() -> None:
    document = Document.from_dict(
        {
            "documentId": "doc-1",
            "text": "本文",
            "metadata": {"productName": "SyntheticProduct"},
        }
    )

    assert document.document_id == "doc-1"
    assert document.metadata.product_name == "SyntheticProduct"


def test_evaluation_case_requires_string_arrays() -> None:
    case = EvaluationCase.from_dict(
        {
            "query_id": "q1",
            "product": "SyntheticProduct",
            "query": "質問",
            "expected_document_ids": ["doc-1"],
            "required_terms": ["質問"],
        }
    )

    assert case.expected_document_ids == ("doc-1",)
    assert case.required_terms == ("質問",)


def test_confidence_outside_unit_interval_is_rejected() -> None:
    try:
        DocumentMetadata(confidence=1.1)
    except ValueError as error:
        assert "confidence" in str(error)
    else:
        raise AssertionError("invalid confidence was accepted")
