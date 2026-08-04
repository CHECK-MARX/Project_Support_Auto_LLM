from __future__ import annotations

import sys
from pathlib import Path


LAB_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(LAB_ROOT / "src"))

from rag_lab.cli import main  # noqa: E402


if __name__ == "__main__":
    raise SystemExit(main())
