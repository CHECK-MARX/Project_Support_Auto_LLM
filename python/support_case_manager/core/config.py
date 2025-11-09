"""
Configuration handling for Support Case Manager.
"""

from __future__ import annotations

from dataclasses import dataclass, field
import json
from pathlib import Path
from typing import Any

from platformdirs import user_data_path

MAX_RECENT_CASES = 20
DEFAULT_STATUSES = ["受付", "調査中", "完了", "クローズ予定", "クローズ"]
DEFAULT_NOTE_TEMPLATES: list[dict[str, str]] = []


@dataclass(slots=True)
class UserSettings:
    base_path: str = ""
    dark_mode: bool = True
    window_geometry: list[int] = field(default_factory=list)
    splitter_state: list[int] = field(default_factory=list)
    recent_cases: list[str] = field(default_factory=list)
    statuses: list[str] = field(default_factory=lambda: list(DEFAULT_STATUSES))
    note_templates: list[dict[str, str]] = field(default_factory=lambda: list(DEFAULT_NOTE_TEMPLATES))

    def update(self, **kwargs: Any) -> "UserSettings":
        for key, value in kwargs.items():
            if hasattr(self, key):
                setattr(self, key, value)
        return self


class ConfigStore:
    def __init__(self, config_dir: Path | None = None) -> None:
        project_root = Path(__file__).resolve().parents[3]
        default_dir = project_root / "config"
        if config_dir:
            self._config_dir = Path(config_dir)
        elif default_dir.exists():
            self._config_dir = default_dir
        else:
            self._config_dir = Path(user_data_path("SupportCaseManager", "itoke"))
        self._config_dir.mkdir(parents=True, exist_ok=True)
        self._path = self._config_dir / "user-settings.json"

    @property
    def path(self) -> Path:
        return self._path

    def load(self) -> UserSettings:
        if not self._path.exists():
            return UserSettings()
        try:
            data = json.loads(self._path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            return UserSettings()
        return UserSettings(
            base_path=data.get("BaseFolder", data.get("BasePath", "")),
            dark_mode=bool(data.get("DarkMode", True)),
            window_geometry=data.get("WindowGeometry", []) or [],
            splitter_state=data.get("SplitterState", []) or [],
            recent_cases=data.get("RecentCases", []) or [],
            statuses=data.get("Statuses", []) or list(DEFAULT_STATUSES),
            note_templates=data.get("NoteTemplates", []) or list(DEFAULT_NOTE_TEMPLATES),
        )

    def save(self, settings: UserSettings) -> None:
        recent = settings.recent_cases[:MAX_RECENT_CASES]
        payload = {
            "BaseFolder": settings.base_path,
            "DarkMode": settings.dark_mode,
            "WindowGeometry": settings.window_geometry,
            "SplitterState": settings.splitter_state,
            "RecentCases": recent,
            "Statuses": settings.statuses or list(DEFAULT_STATUSES),
            "NoteTemplates": settings.note_templates or list(DEFAULT_NOTE_TEMPLATES),
        }
        settings.recent_cases = recent
        self._path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    def add_recent_case(self, settings: UserSettings, folder_path: str) -> None:
        folder_path = folder_path.strip()
        if not folder_path:
            return
        existing = [item for item in settings.recent_cases if item.lower() != folder_path.lower()]
        existing.insert(0, folder_path)
        settings.recent_cases = existing[:MAX_RECENT_CASES]
        self.save(settings)
