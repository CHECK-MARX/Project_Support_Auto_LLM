"""
Application entrypoint for the Support Case Manager.
"""

from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path

from PySide6.QtWidgets import QApplication

from support_case_manager.core.config import ConfigStore
from support_case_manager.core.repository import CaseRepository
from support_case_manager.ui.main_window import MainWindow


def _configure_logging() -> logging.Logger:
    root_dir = Path(__file__).resolve().parents[2]
    log_dir = root_dir / "logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    log_path = log_dir / "SupportCaseManager.log"
    handler_file = logging.FileHandler(log_path, encoding="utf-8", mode="a")
    handler_console = logging.StreamHandler(sys.stdout)
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(name)s :: %(message)s",
        handlers=[handler_file, handler_console],
    )
    logger = logging.getLogger("support_case_manager")
    logger.info("Logger initialized at %s", log_path)
    return logger


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Support Case Manager GUI")
    parser.add_argument("--base-path", help="案件フォルダのベースパスを上書きします。")
    args = parser.parse_args(argv)

    app = QApplication(sys.argv)
    app.setOrganizationName("itoke")
    app.setApplicationName("SupportCaseManager")

    logger = _configure_logging()
    config = ConfigStore()
    settings = config.load()
    if args.base_path:
        settings.base_path = args.base_path
        config.save(settings)

    repository = CaseRepository(logger)
    if settings.base_path:
        repository.set_base_path(settings.base_path)

    window = MainWindow(config, repository, logger)
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
