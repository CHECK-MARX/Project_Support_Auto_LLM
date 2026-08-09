from __future__ import annotations

import argparse
from pathlib import Path

from .regression import (
    run_baseline_candidate_generation,
    run_baseline_candidate_review,
    run_regression_comparison,
    run_tracked_baseline_validation,
)
from .runner import run_evaluation, run_evidence


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Offline RAG quality evaluation for SupportCaseManager"
    )
    subcommands = parser.add_subparsers(dest="command", required=True)
    evaluate = subcommands.add_parser(
        "evaluate", help="run synthetic keyword/BM25 comparison"
    )
    evaluate.add_argument("--config", default="config.example.json")
    evaluate.add_argument("--report-name")
    verify = subcommands.add_parser(
        "verify", help="run evaluation and return nonzero when the quality gate fails"
    )
    verify.add_argument("--config", default="config.example.json")
    verify.add_argument("--report-name")
    compare = subcommands.add_parser(
        "compare", help="compare two generated evaluation reports for regressions"
    )
    compare.add_argument("--baseline", required=True)
    compare.add_argument("--candidate", required=True)
    compare.add_argument("--output-name", default="rag-regression")
    compare.add_argument("--max-quality-drop", type=float, default=0.0)
    compare.add_argument("--max-count-increase", type=int, default=0)
    compare.add_argument("--allow-input-change", action="store_true")
    validate_baseline = subcommands.add_parser(
        "validate-baseline",
        help="validate a tracked synthetic baseline against the compact safe schema",
    )
    validate_baseline.add_argument("--baseline", required=True)
    baseline_candidate = subcommands.add_parser(
        "baseline-candidate",
        help="create a review-only compact baseline candidate from a generated report",
    )
    baseline_candidate.add_argument("--source", required=True)
    baseline_candidate.add_argument("--output-name", default="baseline-candidate")
    review_baseline = subcommands.add_parser(
        "review-baseline",
        help="strictly compare a safe generated candidate with a tracked baseline",
    )
    review_baseline.add_argument("--baseline", required=True)
    review_baseline.add_argument("--candidate", required=True)
    review_baseline.add_argument("--output-name", default="baseline-review")
    evidence = subcommands.add_parser(
        "evidence", help="write top evidence in the future Codex input shape"
    )
    evidence.add_argument("--config", default="config.example.json")
    evidence.add_argument("--query-id", required=True)
    evidence.add_argument(
        "--chunk-strategy",
        choices=("fixed", "paragraph", "heading", "structured"),
        default="heading",
    )
    evidence.add_argument(
        "--search-method",
        choices=("keyword", "bm25", "hash_embedding", "hybrid"),
        default="hybrid",
    )
    evidence.add_argument(
        "--reranker", choices=("none", "lexical"), default="lexical"
    )
    evidence.add_argument(
        "--filter-mode",
        choices=("none", "product", "product_and_version"),
        default="product_and_version",
    )
    evidence.add_argument("--top-k", type=int, choices=range(1, 6), default=3)
    evidence.add_argument("--output-name")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    lab_root = Path(__file__).resolve().parents[2]
    if args.command == "evaluate":
        output = run_evaluation(
            lab_root,
            config_path=args.config,
            report_name=args.report_name,
        )
        print(f"Evaluation rows: {len(output.report['summary'])}")
        for format_name, path in output.files.items():
            print(f"{format_name}: {path.relative_to(lab_root)}")
        return 0
    if args.command == "evidence":
        output = run_evidence(
            lab_root,
            query_id=args.query_id,
            config_path=args.config,
            chunk_strategy=args.chunk_strategy,
            search_method=args.search_method,
            reranker=args.reranker,
            filter_mode=args.filter_mode,
            top_k=args.top_k,
            output_name=args.output_name,
        )
        print(f"Selected evidence: {len(output.payload['selectedEvidence'])}")
        print(f"json: {output.file.relative_to(lab_root)}")
        return 0
    if args.command == "compare":
        output = run_regression_comparison(
            lab_root,
            baseline_path=args.baseline,
            candidate_path=args.candidate,
            output_name=args.output_name,
            max_quality_drop=args.max_quality_drop,
            max_count_increase=args.max_count_increase,
            allow_input_change=args.allow_input_change,
        )
        counts = output.report["counts"]
        print(f"Regression comparison: {output.report['status']}")
        print(
            f"Regressed: {counts['regressed']}, "
            f"missing: {counts['missing']}, improved: {counts['improved']}"
        )
        for format_name, path in output.files.items():
            print(f"{format_name}: {path.relative_to(lab_root)}")
        return 0 if output.report["status"] == "passed" else 1
    if args.command == "validate-baseline":
        output = run_tracked_baseline_validation(
            lab_root,
            baseline_path=args.baseline,
        )
        print("Baseline validation: passed")
        print(f"Fingerprints: {output.fingerprint_count}")
        print(f"Configurations: {output.configuration_count}")
        return 0
    if args.command == "baseline-candidate":
        output = run_baseline_candidate_generation(
            lab_root,
            source_path=args.source,
            output_name=args.output_name,
        )
        print("Baseline candidate: created and validated")
        print(f"json: {output.file.relative_to(lab_root)}")
        print("Review is required before copying it into baselines/.")
        return 0
    if args.command == "review-baseline":
        output = run_baseline_candidate_review(
            lab_root,
            baseline_path=args.baseline,
            candidate_path=args.candidate,
            output_name=args.output_name,
        )
        counts = output.report["counts"]
        print(f"Baseline candidate review: {output.report['status']}")
        print(
            f"Regressed: {counts['regressed']}, "
            f"missing: {counts['missing']}, improved: {counts['improved']}"
        )
        for format_name, path in output.files.items():
            print(f"{format_name}: {path.relative_to(lab_root)}")
        return 0 if output.report["status"] == "passed" else 1
    if args.command == "verify":
        output = run_evaluation(
            lab_root,
            config_path=args.config,
            report_name=args.report_name,
        )
        gate = output.report["quality_gate"]
        recommendation = gate["recommended_configuration"]
        print(f"Quality gate: {gate['status']}")
        print(
            "Recommended: "
            f"chunk={recommendation['chunk_strategy']}, "
            f"search={recommendation['search_method']}, "
            f"reranker={recommendation['reranker']}, "
            f"filter={recommendation['filter_mode']}, "
            f"top_k={recommendation['top_k']}"
        )
        return 0 if gate["status"] == "passed" else 1
    return 2
