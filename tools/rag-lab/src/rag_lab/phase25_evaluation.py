"""Phase 25 production-quality aggregation for the C# live evaluation artifact."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from .phase24_harness import evaluate_live_report


def main() -> int:
    parser = argparse.ArgumentParser(description="Aggregate an anonymized Phase 25 live report")
    parser.add_argument("report", type=Path)
    parser.add_argument("cases", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = evaluate_live_report(args.report, args.cases)
    rendered = json.dumps(result, ensure_ascii=False, indent=2)
    if args.output:
        args.output.write_text(rendered + "\n", encoding="utf-8")
    else:
        print(rendered)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
