from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any, Mapping, Sequence


@dataclass(frozen=True, slots=True)
class QualityGateThresholds:
    min_recall_at_k: float = 0.8
    min_mrr: float = 0.8
    min_ndcg_at_k: float = 0.8
    min_required_term_coverage_at_k: float = 0.8
    max_excluded_term_hit_count: int = 0
    max_product_confusion_count: int = 0
    max_version_mismatch_count: int = 0
    max_mean_search_time_ms: float | None = None

    def __post_init__(self) -> None:
        for name in (
            "min_recall_at_k",
            "min_mrr",
            "min_ndcg_at_k",
            "min_required_term_coverage_at_k",
        ):
            value = getattr(self, name)
            if not 0.0 <= value <= 1.0:
                raise ValueError(f"{name} must be between zero and one")
        for name in (
            "max_excluded_term_hit_count",
            "max_product_confusion_count",
            "max_version_mismatch_count",
        ):
            if getattr(self, name) < 0:
                raise ValueError(f"{name} must be zero or greater")
        if (
            self.max_mean_search_time_ms is not None
            and self.max_mean_search_time_ms <= 0
        ):
            raise ValueError("max_mean_search_time_ms must be greater than zero")

    @classmethod
    def from_mapping(cls, values: Mapping[str, Any]) -> "QualityGateThresholds":
        latency = values.get("maxMeanSearchTimeMs")
        return cls(
            min_recall_at_k=float(values.get("minRecallAtK", 0.8)),
            min_mrr=float(values.get("minMrr", 0.8)),
            min_ndcg_at_k=float(values.get("minNdcgAtK", 0.8)),
            min_required_term_coverage_at_k=float(
                values.get("minRequiredTermCoverageAtK", 0.8)
            ),
            max_excluded_term_hit_count=int(
                values.get("maxExcludedTermHitCount", 0)
            ),
            max_product_confusion_count=int(
                values.get("maxProductConfusionCount", 0)
            ),
            max_version_mismatch_count=int(
                values.get("maxVersionMismatchCount", 0)
            ),
            max_mean_search_time_ms=float(latency) if latency is not None else None,
        )

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def assess_summary_row(
    row: Mapping[str, Any], thresholds: QualityGateThresholds
) -> tuple[bool, tuple[str, ...]]:
    violations: list[str] = []
    checks = (
        ("mean_recall_at_k", thresholds.min_recall_at_k, "Recall@K"),
        ("mrr", thresholds.min_mrr, "MRR"),
        ("mean_ndcg_at_k", thresholds.min_ndcg_at_k, "nDCG@K"),
        (
            "mean_required_term_coverage_at_k",
            thresholds.min_required_term_coverage_at_k,
            "required-term coverage",
        ),
    )
    for key, minimum, label in checks:
        actual = float(row.get(key, 0.0))
        if actual < minimum:
            violations.append(f"{label} {actual:.6f} is below {minimum:.6f}")
    maximum_checks = (
        (
            "excluded_term_hit_count",
            thresholds.max_excluded_term_hit_count,
            "excluded-term hits",
        ),
        (
            "product_confusion_count",
            thresholds.max_product_confusion_count,
            "product confusions",
        ),
        (
            "version_mismatch_count",
            thresholds.max_version_mismatch_count,
            "version mismatches",
        ),
    )
    for key, maximum, label in maximum_checks:
        actual = int(row.get(key, 0))
        if actual > maximum:
            violations.append(f"{label} {actual} exceeds {maximum}")
    if thresholds.max_mean_search_time_ms is not None:
        actual_latency = float(row.get("mean_search_time_ms", 0.0))
        if actual_latency > thresholds.max_mean_search_time_ms:
            violations.append(
                "mean search time "
                f"{actual_latency:.6f} ms exceeds "
                f"{thresholds.max_mean_search_time_ms:.6f} ms"
            )
    return not violations, tuple(violations)


def apply_quality_gate(
    summary_rows: Sequence[Mapping[str, Any]],
    thresholds: QualityGateThresholds,
    *,
    preferred_top_k: int,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    if preferred_top_k <= 0:
        raise ValueError("preferred_top_k must be greater than zero")
    annotated: list[dict[str, Any]] = []
    for source_row in summary_rows:
        row = dict(source_row)
        passed, violations = assess_summary_row(row, thresholds)
        row["quality_gate_passed"] = passed
        row["quality_gate_violations"] = list(violations)
        annotated.append(row)
    candidates = [row for row in annotated if row.get("top_k") == preferred_top_k]
    if not candidates:
        raise ValueError("preferred_top_k is not present in evaluation summary")
    filter_preference = {"product_and_version": 0, "product": 1, "none": 2}
    search_preference = {"bm25": 0, "keyword": 1, "hybrid": 2, "hash_embedding": 3}
    reranker_preference = {"none": 0, "lexical": 1}
    chunk_preference = {"fixed": 0, "paragraph": 1, "heading": 2, "structured": 3}
    ranked = sorted(
        candidates,
        key=lambda row: (
            not bool(row["quality_gate_passed"]),
            -float(row.get("mean_recall_at_k", 0.0)),
            -float(row.get("mrr", 0.0)),
            -float(row.get("mean_ndcg_at_k", 0.0)),
            -float(row.get("mean_required_term_coverage_at_k", 0.0)),
            int(row.get("excluded_term_hit_count", 0)),
            int(row.get("product_confusion_count", 0)),
            int(row.get("version_mismatch_count", 0)),
            filter_preference.get(str(row.get("filter_mode")), 99),
            search_preference.get(str(row.get("search_method")), 99),
            reranker_preference.get(str(row.get("reranker")), 99),
            chunk_preference.get(str(row.get("chunk_strategy")), 99),
            str(row.get("chunk_strategy", "")),
            str(row.get("search_method", "")),
            str(row.get("reranker", "")),
        ),
    )
    recommended = ranked[0]
    passing_count = sum(bool(row["quality_gate_passed"]) for row in candidates)
    identity_keys = (
        "chunk_strategy",
        "search_method",
        "reranker",
        "filter_mode",
        "top_k",
        "mean_recall_at_k",
        "mrr",
        "mean_ndcg_at_k",
        "mean_required_term_coverage_at_k",
        "excluded_term_hit_count",
        "product_confusion_count",
        "version_mismatch_count",
        "mean_search_time_ms",
        "quality_gate_passed",
        "quality_gate_violations",
    )
    gate = {
        "status": "passed" if recommended["quality_gate_passed"] else "failed",
        "preferred_top_k": preferred_top_k,
        "thresholds": thresholds.to_dict(),
        "candidate_count": len(candidates),
        "passing_candidate_count": passing_count,
        "recommended_configuration": {
            key: recommended.get(key) for key in identity_keys
        },
    }
    return annotated, gate
