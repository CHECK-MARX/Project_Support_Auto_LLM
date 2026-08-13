from rag_lab.source_ingestion import (
    SourceIngestionObservation,
    evaluate_source_ingestion,
    is_valid_archive_source_path,
)


def test_source_ingestion_reports_false_exclusion_and_false_inclusion():
    report = evaluate_source_ingestion(
        (
            _item("manual", True, False, "C:/manuals/Installation Notes.pdf"),
            _item("trace", False, True, "C:/manuals/trace.txt"),
        )
    )
    assert report.false_negative_ids == ("manual",)
    assert report.false_positive_ids == ("trace",)
    assert not report.passed


def test_source_ingestion_detects_duplicate_and_external_preference():
    clean = evaluate_source_ingestion(
        (
            _item("external", True, True, "C:/manuals/manual.pdf", "abc"),
            _item("archive", False, False, "C:/manuals/archive.zip!/docs/manual.pdf", "abc"),
        )
    )
    assert clean.duplicate_content_groups == (("archive", "external"),)
    assert not clean.duplicate_index_violations
    assert not clean.external_preference_violations
    assert clean.passed

    violation = evaluate_source_ingestion(
        (
            _item("external", True, False, "C:/manuals/manual.pdf", "abc"),
            _item("archive", True, True, "C:/manuals/archive.zip!/docs/manual.pdf", "abc"),
        )
    )
    assert violation.external_preference_violations == ("abc",)


def test_archive_source_path_requires_safe_zip_and_entry_paths():
    assert is_valid_archive_source_path("C:/manuals/archive.zip!/docs/manual.pdf")
    assert not is_valid_archive_source_path("C:/manuals/archive.zip!/../evil.txt")
    assert not is_valid_archive_source_path("C:/manuals/archive.zip!/C:/evil.txt")
    assert not is_valid_archive_source_path("C:/manuals/archive.7z!/docs/manual.pdf")


def _item(
    case_id: str,
    expected: bool,
    actual: bool,
    source_path: str,
    sha256: str = "",
) -> SourceIngestionObservation:
    return SourceIngestionObservation(case_id, expected, actual, source_path, sha256)
