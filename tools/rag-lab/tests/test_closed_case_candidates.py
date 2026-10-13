from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from build_closed_case_candidates import build


def test_closed_case_candidates_are_masked_and_split_by_case(tmp_path: Path) -> None:
    roots = {"QAC": tmp_path / "qac", "CHECKMARX": tmp_path / "checkmarx"}
    for product, root in roots.items():
        for number in range(3):
            case = root / "closed" / f"case-{number}"
            case.mkdir(parents=True)
            (case / "お客様ご相談内容.txt").write_text(
                f"{product} の設定項目 {number} の意味と確認手順を教えてください。"
                "管理画面で表示される値の読み方も知りたいです。\n"
                "株式会社サンプル 山田様 sample@example.com\n",
                encoding="utf-8",
            )
            (case / "お客様への返信案.txt").write_text(
                f"設定項目 {number} の意味はこの合成テスト用の値です。"
                "管理画面で当該項目を開き、表示された値を確認してください。"
                "実際の設定を変更する前に、手順書の該当箇所を確認してください。"
                "この文章は架空のテストデータであり、実製品の仕様を示しません。\n"
                "株式会社サンプル 山田様 sample@example.com\n",
                encoding="utf-8",
            )

    output = tmp_path / "generated" / "candidates.json"
    summary = build(roots, output, per_product=3)
    first = output.read_bytes()
    build(roots, output, per_product=3)
    document = json.loads(first)

    assert output.read_bytes() == first
    assert summary["development"] == 4
    assert summary["holdout"] == 2
    assert len(document["cases"]) == 6
    assert all(
        sum(item["product"] == product and item["split"] == "development"
            for item in document["cases"]) == 2
        and sum(item["product"] == product and item["split"] == "holdout"
                for item in document["cases"]) == 1
        for product in roots
    )
    assert all(not item["humanPrivacyReviewed"] and not item["humanAnswerReviewed"]
               for item in document["cases"])
    assert b"sample@example.com" not in first
    assert "株式会社サンプル" not in first.decode("utf-8")
    assert str(tmp_path) not in first.decode("utf-8")


def test_signature_after_question_is_removed_with_following_lines() -> None:
    from build_closed_case_candidates import _sanitize

    text = (
        "QACの設定値を確認する手順と、結果の読み方を教えてください。\n"
        "検証環境は demo.internal.cloudapp.net です。\n"
        "TOYO DEVELOPMENT PORTAL を参照しました。\n"
        "添付の 顧客名_設定.xlsx を確認してください。\n"
        "佐藤 太郎 様\n"
        "架空企業 開発センター\n"
        "連絡先: sample@example.com\n"
    )
    assert _sanitize(text) == (
        "QACの設定値を確認する手順と、結果の読み方を教えてください。\n"
        "検証環境は [HOSTNAME] です。"
        "\n[ORGANIZATION_RESOURCE] を参照しました。"
        "\n添付の [ATTACHMENT] を確認してください。"
    )


def test_three_product_selection_keeps_case_level_split(tmp_path: Path) -> None:
    roots = {product: tmp_path / product for product in ("QAC", "CHECKMARX", "KLOCWORK")}
    for product, root in roots.items():
        for number in range(3):
            case = root / "closed" / f"case-{number}"
            case.mkdir(parents=True)
            (case / "お客様ご相談内容.txt").write_text(
                f"{product} の項目 {number} を設定する手順と、画面の表示結果を確認する方法を教えてください。",
                encoding="utf-8",
            )
            (case / "お客様への返信案.txt").write_text(
                f"{product} の項目 {number} の設定画面を開いて値を確認してください。"
                "表示結果と手順書の該当箇所を照合し、変更前の値も記録してください。"
                "この文章は架空のテストデータです。",
                encoding="utf-8",
            )

    output = tmp_path / "candidates.json"
    summary = build(roots, output, selection_counts={
        "QAC": 3, "CHECKMARX": 3, "KLOCWORK": 2,
    }, development_counts={
        "QAC": 2, "CHECKMARX": 2, "KLOCWORK": 1,
    })
    document = json.loads(output.read_text(encoding="utf-8"))
    assert len(document["cases"]) == 8
    assert (summary["development"], summary["holdout"]) == (5, 3)
    assert sum(item["product"] == "KLOCWORK" for item in document["cases"]) == 2
    assert len({item["candidateId"] for item in document["cases"]}) == 8
