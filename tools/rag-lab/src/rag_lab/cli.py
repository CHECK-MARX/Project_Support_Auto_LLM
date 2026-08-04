from __future__ import annotations

import argparse
from pathlib import Path

from .runner import run_evaluation


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
    return 2
