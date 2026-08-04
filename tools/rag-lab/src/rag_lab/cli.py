from __future__ import annotations

import argparse
from pathlib import Path

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
    return 2
