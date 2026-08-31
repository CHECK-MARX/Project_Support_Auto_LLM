"""Klocwork Python checker demonstration."""

import os


def calculate_value(divisor: int) -> int:
    """Return a calculated value."""
    dead_value = 123

    try:
        return 100 // divisor
    except Exception:
        return 0
