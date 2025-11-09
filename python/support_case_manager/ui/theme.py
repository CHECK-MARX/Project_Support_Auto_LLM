"""
Qt palette helpers.
"""

from __future__ import annotations

from PySide6.QtGui import QColor, QPalette
from PySide6.QtWidgets import QApplication


def dark_palette() -> QPalette:
    palette = QPalette()
    palette.setColor(QPalette.Window, QColor(32, 34, 38))
    palette.setColor(QPalette.WindowText, QColor(230, 233, 240))
    palette.setColor(QPalette.Base, QColor(44, 46, 51))
    palette.setColor(QPalette.AlternateBase, QColor(50, 52, 58))
    palette.setColor(QPalette.ToolTipBase, QColor(245, 246, 250))
    palette.setColor(QPalette.ToolTipText, QColor(33, 37, 41))
    palette.setColor(QPalette.Text, QColor(230, 233, 240))
    palette.setColor(QPalette.Button, QColor(56, 58, 64))
    palette.setColor(QPalette.ButtonText, QColor(230, 233, 240))
    palette.setColor(QPalette.BrightText, QColor(255, 0, 0))
    palette.setColor(QPalette.Highlight, QColor(10, 132, 255))
    palette.setColor(QPalette.HighlightedText, QColor(255, 255, 255))
    return palette


def apply_theme(app: QApplication, dark_mode: bool) -> None:
    if dark_mode:
        app.setStyle("Fusion")
        app.setPalette(dark_palette())
    else:
        app.setPalette(app.style().standardPalette())

