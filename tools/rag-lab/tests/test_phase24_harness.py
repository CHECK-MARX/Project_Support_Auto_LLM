from collections import Counter
from pathlib import Path

from rag_lab.phase24_harness import load_phase24_cases


def test_phase24_corpus_is_structured_and_balanced() -> None:
    path = Path(__file__).parents[1] / "phase24-2-answer-quality-cases.json"
    cases = load_phase24_cases(path)

    assert len(cases) == 16
    assert Counter(case.product for case in cases) == {
        "HelixQAC": 4,
        "Checkmarx One": 4,
        "Validate": 4,
        "Klocwork": 4,
    }
    assert Counter(case.question_type for case in cases)["HowTo"] >= 1
    assert all(case.values["requiredCoverage"] for case in cases)
