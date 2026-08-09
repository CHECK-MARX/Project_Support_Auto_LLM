from __future__ import annotations

import json
from pathlib import Path
import statistics
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))
from rag_lab.coverage_set_selection import select_coverage_evidence

sys.path.insert(0, str(Path(__file__).resolve().parent / "tests"))
from test_phase18_coverage_selection import _request  # noqa: E402


WARMUP = 100
SAMPLES = 10_000
fixture_path = Path(__file__).resolve().parent / "samples" / "phase18_coverage_selection_cases.json"
case = json.loads(fixture_path.read_text(encoding="utf-8"))[0]
request = _request(case)

for _ in range(WARMUP):
    select_coverage_evidence(request)

timings = []
for _ in range(SAMPLES):
    started = time.perf_counter_ns()
    select_coverage_evidence(request)
    timings.append((time.perf_counter_ns() - started) / 1_000_000)

timings.sort()
print(
    json.dumps(
        {
            "engine": "PythonAlgorithm",
            "warmup": WARMUP,
            "samples": SAMPLES,
            "medianMs": statistics.median(timings),
            "p95Ms": timings[int((len(timings) - 1) * 0.95 + 0.999999)],
            "processStartupIncluded": False,
        }
    )
)
