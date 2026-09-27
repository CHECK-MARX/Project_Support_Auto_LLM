from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from finalize_closed_case_review import validate_row


def test_blank_review_cannot_be_approved() -> None:
    missing, approved = validate_row({})
    assert len(missing) == 9
    assert "匿名化確認" in missing
    assert "必要な根拠" in missing
    assert approved == {}


def test_complete_review_requires_explicit_proof_and_reviewer() -> None:
    fields = {
        "F": "確認済", "G": "確認済", "H": "必要条件を確認する", "I": "なし",
        "J": "公式資料の節 3.2", "K": "NeedsReview", "L": "送信記録 2026-09-01",
        "M": "テスト担当", "N": "2026-09-26",
    }
    missing, approved = validate_row(fields)
    assert missing == []
    assert approved["forbiddenClaims"] == []
    assert approved["expectedClaims"] == ["必要条件を確認する"]
    assert approved["reviewDate"] == "2026-09-26"

    fields["L"] = "未確認"
    missing, _ = validate_row(fields)
    assert missing == ["送信済み・承認済み回答の証跡"]

    fields["L"] = "送信記録 2026-09-01"
    fields["M"] = "Codex一次監査"
    missing, _ = validate_row(fields)
    assert missing == ["確認者"]
