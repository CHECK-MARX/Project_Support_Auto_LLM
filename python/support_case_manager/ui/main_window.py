"""
PySide6 main window for Support Case Manager.
"""

from __future__ import annotations

from datetime import datetime
import os
from pathlib import Path
from typing import Optional

from PySide6.QtCore import QDate, Qt
from PySide6.QtGui import QCloseEvent, QKeySequence, QShortcut
from PySide6.QtWidgets import (
    QApplication,
    QCheckBox,
    QComboBox,
    QDialog,
    QDateEdit,
    QFileDialog,
    QGridLayout,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMainWindow,
    QMessageBox,
    QPushButton,
    QPlainTextEdit,
    QVBoxLayout,
    QTextEdit,
    QWidget,
    QInputDialog,
)

from support_case_manager.core.cases import CaseRecord, build_folder_name, parse_case_from_directory
from support_case_manager.core.config import ConfigStore, UserSettings, DEFAULT_STATUSES
from support_case_manager.core.notes import NOTE_DEFINITIONS, NoteDefinition, append_note, create_subfolder, ensure_note_file
from support_case_manager.core.repository import CaseRepository
from support_case_manager.ui.theme import apply_theme


class MainWindow(QMainWindow):
    def __init__(self, config: ConfigStore, repository: CaseRepository, logger) -> None:
        super().__init__()
        self._config = config
        self._settings: UserSettings = config.load()
        self._repository = repository
        self._logger = logger
        self._case_cache: dict[str, CaseRecord] = {}
        self._current_case: Optional[CaseRecord] = None
        self._current_note: NoteDefinition = NOTE_DEFINITIONS[0]

        self._build_ui()
        self._bind_shortcuts()
        self._wire_events()
        self._restore_state()
        self._refresh_status_options()
        self._refresh_template_combo()
        self._apply_theme(self._settings.dark_mode)
        self.refresh_view()

    # ------------------------------------------------------------------ construction
    def _build_ui(self) -> None:
        self.setWindowTitle("サポート受付ディレクトリ作成ツール (PySide6)")
        self.resize(1200, 820)
        central = QWidget(self)
        root = QVBoxLayout(central)
        root.setContentsMargins(16, 16, 16, 16)
        root.setSpacing(12)

        # Base folder row
        base_row = QHBoxLayout()
        base_row.addWidget(QLabel("ベースフォルダ:"))
        self.base_path_edit = QLineEdit(self._settings.base_path)
        self.base_path_edit.setPlaceholderText("例: C:\\SupportCases")
        base_row.addWidget(self.base_path_edit, 1)
        self.base_browse_button = QPushButton("参照…")
        base_row.addWidget(self.base_browse_button)
        self.history_refresh_button = QPushButton("履歴更新")
        base_row.addWidget(self.history_refresh_button)
        root.addLayout(base_row)

        # History + controls
        history_row = QHBoxLayout()
        history_row.addWidget(QLabel("履歴:"))
        self.history_combo = QComboBox()
        self.history_combo.setSizeAdjustPolicy(QComboBox.AdjustToContents)
        history_row.addWidget(self.history_combo, 1)
        self.new_button = QPushButton("新規")
        history_row.addWidget(self.new_button)
        self.open_folder_button = QPushButton("開く")
        self.open_folder_button.setEnabled(False)
        history_row.addWidget(self.open_folder_button)
        root.addLayout(history_row)

        # Search row
        search_row = QHBoxLayout()
        search_row.addWidget(QLabel("サポート番号検索:"))
        self.search_edit = QLineEdit()
        self.search_edit.setPlaceholderText("例: 00001234")
        search_row.addWidget(self.search_edit, 1)
        self.search_button = QPushButton("検索")
        search_row.addWidget(self.search_button)
        root.addLayout(search_row)

        # Form group
        form_group = QGroupBox("案件メタ情報")
        form_layout = QGridLayout(form_group)
        form_layout.setSpacing(8)

        self.date_edit = QDateEdit()
        self.date_edit.setCalendarPopup(True)
        self.date_edit.setDate(QDate.currentDate())
        self.company_edit = QLineEdit()
        self.company_edit.setPlaceholderText("会社名を入力")
        self.support_edit = QLineEdit()
        self.support_edit.setPlaceholderText("00001234")
        self.status_combo = QComboBox()
        self.status_combo.setEditable(True)
        self.status_add_button = QPushButton("＋")
        self.status_add_button.setFixedWidth(32)
        self.status_remove_button = QPushButton("－")
        self.status_remove_button.setFixedWidth(32)
        self.category_combo = QComboBox()
        self.category_refresh_button = QPushButton("ステータス更新")
        self.preview_edit = QLineEdit()
        self.preview_edit.setReadOnly(True)
        self.preview_edit.setText("(必須項目を入力してください)")
        self.preview_edit.setFocusPolicy(Qt.ClickFocus)
        self.preview_edit.setContextMenuPolicy(Qt.ActionsContextMenu)

        form_layout.addWidget(QLabel("受付日"), 0, 0)
        form_layout.addWidget(self.date_edit, 0, 1)
        form_layout.addWidget(QLabel("会社名"), 0, 2)
        form_layout.addWidget(self.company_edit, 0, 3)
        form_layout.addWidget(QLabel("サポート番号"), 1, 0)
        form_layout.addWidget(self.support_edit, 1, 1)
        form_layout.addWidget(QLabel("ステータス"), 1, 2)
        form_layout.addWidget(self.status_combo, 1, 3)
        form_layout.addWidget(self.status_add_button, 1, 4)
        form_layout.addWidget(self.status_remove_button, 1, 5)
        form_layout.addWidget(QLabel("保存先フォルダ"), 2, 0)
        form_layout.addWidget(self.category_combo, 2, 1, 1, 3)
        form_layout.addWidget(self.category_refresh_button, 2, 4)
        form_layout.addWidget(QLabel("作成フォルダ名"), 3, 0)
        form_layout.addWidget(self.preview_edit, 3, 1, 1, 4)
        form_layout.setColumnStretch(1, 1)
        form_layout.setColumnStretch(3, 1)
        form_layout.setColumnStretch(4, 0)

        root.addWidget(form_group)

        # Checkboxes
        options_row = QHBoxLayout()
        self.open_after_check = QCheckBox("作成後にフォルダを開く")
        self.open_after_check.setChecked(True)
        options_row.addWidget(self.open_after_check)
        self.dark_mode_check = QCheckBox("ダークモード")
        self.dark_mode_check.setChecked(self._settings.dark_mode)
        options_row.addWidget(self.dark_mode_check)
        options_row.addStretch(1)
        root.addLayout(options_row)

        # Note editor
        note_group = QGroupBox("ノート編集")
        note_layout = QVBoxLayout(note_group)

        note_header = QHBoxLayout()
        note_header.addWidget(QLabel("ノート種別:"))
        self.note_selector = QComboBox()
        for note in NOTE_DEFINITIONS:
            self.note_selector.addItem(note.label, note.key)
        note_header.addWidget(self.note_selector, 1)
        note_header.addWidget(QLabel("テンプレート:"))
        self.template_combo = QComboBox()
        self.template_combo.setPlaceholderText("テンプレートを選択")
        note_header.addWidget(self.template_combo, 1)
        self.template_save_button = QPushButton("テンプレート保存")
        note_header.addWidget(self.template_save_button)
        note_layout.addLayout(note_header)

        self.note_file_label = QLabel("ファイル: -")
        note_layout.addWidget(self.note_file_label)

        self.note_editor = QPlainTextEdit()
        self.note_editor.setPlaceholderText("追記する内容を入力してください。")
        note_layout.addWidget(self.note_editor, 1)

        note_buttons = QHBoxLayout()
        self.note_open_button = QPushButton("ノートを開く")
        self.note_append_button = QPushButton("追記保存")
        self.note_subfolder_button = QPushButton("サブフォルダ作成")
        note_buttons.addWidget(self.note_open_button)
        note_buttons.addWidget(self.note_append_button)
        note_buttons.addWidget(self.note_subfolder_button)
        note_buttons.addStretch(1)
        note_layout.addLayout(note_buttons)

        root.addWidget(note_group, 1)

        # Action buttons
        action_row = QHBoxLayout()
        action_row.addStretch(1)
        self.create_button = QPushButton("フォルダ作成")
        action_row.addWidget(self.create_button)
        root.addLayout(action_row)

        self.statusBar().showMessage("準備完了")
        self.setCentralWidget(central)

        self.setStyleSheet(
            """
            QLineEdit[error="true"], QComboBox[error="true"] {
                border: 1px solid #d9534f;
            }
            """
        )

    def _bind_shortcuts(self) -> None:
        QShortcut(QKeySequence("Ctrl+S"), self, activated=self._on_note_append)
        QShortcut(QKeySequence("Ctrl+O"), self, activated=self._on_note_open)
        QShortcut(QKeySequence("Ctrl+N"), self, activated=self._on_new_case)
        QShortcut(QKeySequence("Ctrl+F"), self, activated=lambda: self.search_edit.setFocus())

    def _wire_events(self) -> None:
        self.base_browse_button.clicked.connect(self._on_browse_base)
        self.base_path_edit.editingFinished.connect(self._on_base_path_changed)
        self.history_refresh_button.clicked.connect(self.refresh_view)
        self.history_combo.currentIndexChanged.connect(self._on_history_selected)
        self.new_button.clicked.connect(self._on_new_case)
        self.open_folder_button.clicked.connect(self._on_open_folder)
        self.search_button.clicked.connect(self._on_search)
        self.search_edit.returnPressed.connect(self._on_search)
        self.date_edit.dateChanged.connect(self._update_preview)
        self.company_edit.textChanged.connect(self._update_preview)
        self.support_edit.textChanged.connect(self._on_support_changed)
        self.status_combo.currentTextChanged.connect(self._update_preview)
        self.status_add_button.clicked.connect(self._on_status_add)
        self.status_remove_button.clicked.connect(self._on_status_remove)
        self.category_refresh_button.clicked.connect(self._on_status_update)
        self.category_combo.currentTextChanged.connect(self._on_category_selected)
        self.dark_mode_check.toggled.connect(self._on_theme_toggled)
        self.create_button.clicked.connect(self._on_create_case)
        self.note_selector.currentIndexChanged.connect(self._on_note_changed)
        self.note_open_button.clicked.connect(self._on_note_open)
        self.note_append_button.clicked.connect(self._on_note_append)
        self.note_subfolder_button.clicked.connect(self._on_note_subfolder)
        self.template_combo.activated.connect(self._on_template_selected)
        self.template_save_button.clicked.connect(self._on_template_save)

    def _restore_state(self) -> None:
        if self._settings.window_geometry:
            self.restoreGeometry(bytes(self._settings.window_geometry))
        if self._settings.splitter_state:
            # legacy support (no splitter now)
            pass
        self._update_preview()
        self._update_note_file_label()

    def _refresh_status_options(self, selected: str | None = None) -> None:
        options = self._settings.statuses or list(DEFAULT_STATUSES)
        unique: list[str] = []
        for option in options:
            clean = option.strip()
            if clean and clean not in unique:
                unique.append(clean)
        if not unique:
            unique = list(DEFAULT_STATUSES)
        self._settings.statuses = unique
        self.status_combo.blockSignals(True)
        self.status_combo.clear()
        self.status_combo.addItems(unique)
        if selected and selected in unique:
            self.status_combo.setCurrentText(selected)
        elif unique:
            self.status_combo.setCurrentIndex(0)
        self.status_combo.blockSignals(False)

    def _refresh_template_combo(self, selected_name: str | None = None) -> None:
        templates = self._settings.note_templates or []
        self.template_combo.blockSignals(True)
        self.template_combo.clear()
        for entry in templates:
            self.template_combo.addItem(entry.get("name", "(名称未設定)"), entry)
        if selected_name:
            index = next((i for i, entry in enumerate(templates) if entry.get("name") == selected_name), -1)
            self.template_combo.setCurrentIndex(index)
        elif templates:
            self.template_combo.setCurrentIndex(0)
        self.template_combo.blockSignals(False)

    # ------------------------------------------------------------------ actions
    def refresh_view(self) -> None:
        base = self.base_path_edit.text().strip()
        if base:
            try:
                self._repository.set_base_path(base)
                self._settings.base_path = base
                self._config.save(self._settings)
            except OSError as exc:
                QMessageBox.critical(self, "エラー", f"フォルダへアクセスできません: {exc}")
                return
        if not self._repository.base_path:
            return
        cases = self._repository.all_cases()
        self._case_cache = {str(case.folder_path): case for case in cases}
        self._load_categories()
        self._load_history()
        self.statusBar().showMessage("履歴を更新しました。", 3000)

    def _on_browse_base(self) -> None:
        dialog = QFileDialog(self, "ベースフォルダを選択")
        dialog.setFileMode(QFileDialog.Directory)
        if dialog.exec():
            selected = dialog.selectedFiles()
            if selected:
                self.base_path_edit.setText(selected[0])
                self.refresh_view()

    def _on_base_path_changed(self) -> None:
        self.refresh_view()

    def _load_categories(self) -> None:
        base = self._repository.base_path
        current_text = self.category_combo.currentText()
        self._category_paths: dict[str, Path] = {}
        self.category_combo.blockSignals(True)
        self.category_combo.clear()
        self.category_combo.addItem("(ベース直下)", str(base) if base else "")
        if base and base.exists():
            entries = [entry for entry in base.iterdir() if entry.is_dir()]
            entries.sort(key=lambda p: p.name)
            for entry in entries:
                self.category_combo.addItem(entry.name, str(entry))
                self._category_paths[entry.name] = entry
        index = self.category_combo.findText(current_text)
        self.category_combo.setCurrentIndex(index if index >= 0 else 0)
        self.category_combo.blockSignals(False)
        self._update_preview()

    def _on_status_update(self) -> None:
        if not self._current_case:
            QMessageBox.information(self, "情報", "更新する案件を選択してください。")
            return
        new_status = self.status_combo.currentText().strip()
        if not new_status:
            QMessageBox.warning(self, "エラー", "ステータスを選択してください。")
            return

        folder = self._current_case.folder_path
        old_path = str(folder)
        try:
            base = folder.parent
            new_name = build_folder_name(
                self.date_edit.date().toString("yyyyMMdd"),
                self.company_edit.text(),
                self.support_edit.text(),
                new_status,
                datetime.now().strftime("%Y%m%d"),
            )
            new_path = base / new_name
            if new_path.exists():
                QMessageBox.warning(self, "エラー", "同名のフォルダが既に存在します。")
                return
            folder.rename(new_path)
            updated_case = CaseRecord(
                company=self.company_edit.text(),
                support_number=self.support_edit.text(),
                status=new_status,
                created_on=self.date_edit.date().toString("yyyyMMdd"),
                folder_name=new_name,
                folder_path=new_path,
                last_updated=datetime.utcnow().isoformat(),
                category=self.category_combo.currentText() if self.category_combo.currentIndex() > 0 else "",
            )
            self._current_case = updated_case
            new_path_str = str(new_path)
            self._case_cache.pop(old_path, None)
            self._case_cache[new_path_str] = updated_case
            self._settings.recent_cases = [
                item for item in self._settings.recent_cases if item.lower() != old_path.lower()
            ]
            self._config.save(self._settings)
            self._config.add_recent_case(self._settings, new_path_str)
            self._repository.update_case_entry(updated_case)
            self.preview_edit.setText(new_name)
            self.refresh_view()
            QMessageBox.information(self, "完了", "フォルダ名を更新しました。")
        except Exception as exc:  # noqa: BLE001
            QMessageBox.critical(self, "エラー", f"ステータス更新に失敗しました: {exc}")

    def _on_category_selected(self, text: str) -> None:
        self._update_preview()
        folder_text = text.strip()
        base = self._repository.base_path
        if folder_text == "(ベース直下)" or not folder_text or not base:
            return
        target = self._category_paths.get(folder_text)
        if not target or not target.exists():
            return
        case = parse_case_from_directory(target)
        if case:
            self._set_current_case(case, persist=False)

    def _load_history(self) -> None:
        self.history_combo.blockSignals(True)
        self.history_combo.clear()
        for path in self._settings.recent_cases:
            record = self._case_cache.get(path)
            if not record:
                folder = Path(path)
                if folder.exists():
                    record = parse_case_from_directory(folder)
                    if record:
                        self._case_cache[path] = record
            if record and record.status == "クローズ":
                continue
            label = record.display_text() if record else Path(path).name
            if "クローズ" in label:
                continue
            self.history_combo.addItem(label, path)
        self.history_combo.setCurrentIndex(-1)
        self.history_combo.blockSignals(False)

    def _on_history_selected(self, index: int) -> None:
        if index < 0:
            return
        path = self.history_combo.itemData(index)
        if not path:
            return
        folder = Path(path)
        if not folder.exists():
            self._settings.recent_cases = [item for item in self._settings.recent_cases if item != path]
            self._config.save(self._settings)
            self._load_history()
            QMessageBox.warning(self, "エラー", "フォルダが存在しないため、履歴から削除しました。")
            return
        case = self._case_cache.get(str(folder))
        if not case:
            parsed = parse_case_from_directory(folder)
            case = parsed
        if not case:
            QMessageBox.warning(self, "エラー", "フォルダ名から案件情報を読み取れませんでした。")
            return
        self._set_current_case(case)

    def _on_new_case(self) -> None:
        self._current_case = None
        self.company_edit.clear()
        self.support_edit.clear()
        self.status_combo.setCurrentIndex(0)
        self.date_edit.setDate(QDate.currentDate())
        self.category_combo.setCurrentIndex(0)
        self.preview_edit.setText("(必須項目を入力してください)")
        self.note_editor.clear()
        self.open_folder_button.setEnabled(False)
        self.note_file_label.setText("ファイル: -")
        self.statusBar().showMessage("新規案件モードです。", 3000)

    def _on_open_folder(self) -> None:
        if not self._current_case:
            QMessageBox.information(self, "情報", "案件を選択してください。")
            return
        folder = self._current_case.folder_path
        try:
            if os.name == "nt":
                os.startfile(str(folder))  # type: ignore[attr-defined]
            else:  # pragma: no cover
                import subprocess

                subprocess.Popen(["xdg-open", str(folder)])
        except OSError as exc:
            QMessageBox.critical(self, "エラー", f"フォルダを開けません: {exc}")

    def _on_pick_case_folder(self) -> None:
        dialog = QFileDialog(self, "案件フォルダを選択")
        dialog.setFileMode(QFileDialog.Directory)
        if self._repository.base_path:
            dialog.setDirectory(str(self._repository.base_path))
        if not dialog.exec():
            return
        selected = dialog.selectedFiles()
        if not selected:
            return
        folder = Path(selected[0]).resolve()
        case = parse_case_from_directory(folder)
        if not case:
            QMessageBox.warning(self, "エラー", "フォルダ名から案件情報を読み取れませんでした。")
            return

        new_base = folder.parent
        current_base_text = self.base_path_edit.text().strip()
        refresh_needed = True
        if current_base_text:
            try:
                refresh_needed = Path(current_base_text).resolve() != new_base
            except OSError:
                refresh_needed = True

        if refresh_needed:
            self.base_path_edit.setText(str(new_base))
            self.refresh_view()
        else:
            self._case_cache[str(folder)] = case

        cached_case = self._case_cache.get(str(folder)) or case
        self._repository.update_case_entry(cached_case)
        self._set_current_case(cached_case)
        self.statusBar().showMessage("フォルダから案件を読み込みました。", 4000)

    def _on_search(self) -> None:
        target = self.search_edit.text().strip()
        if not target:
            QMessageBox.information(self, "情報", "サポート番号を入力してください。")
            return
        case = self._repository.find_by_support(target)
        if not case:
            QMessageBox.information(self, "情報", "該当案件が見つかりませんでした。")
            return
        self._set_current_case(case)
        self.statusBar().showMessage("案件を読み込みました。", 3000)

    def _on_create_case(self) -> None:
        if not self._validate_required():
            QMessageBox.warning(self, "エラー", "必須項目を入力してください。")
            return
        self._ensure_status_option(self.status_combo.currentText())
        try:
            case = self._repository.create_case(
                company=self.company_edit.text().strip(),
                support_number=self.support_edit.text().strip(),
                status=self.status_combo.currentText(),
                created_on=self.date_edit.date().toString("yyyyMMdd"),
                category=self.category_combo.currentData() or "",
                open_after=self.open_after_check.isChecked(),
            )
        except Exception as exc:  # noqa: BLE001
            self._logger.error("Failed to create case: %s", exc)
            QMessageBox.critical(self, "エラー", str(exc))
            return
        self._set_current_case(case)
        self.refresh_view()
        self.statusBar().showMessage("案件フォルダを作成しました。", 5000)

    # ------------------------------------------------------------------ note handling
    def _current_note_folder(self) -> Optional[Path]:
        if not self._current_case:
            return None
        return self._current_case.folder_path

    def _on_note_changed(self) -> None:
        key = self.note_selector.currentData()
        self._current_note = next((n for n in NOTE_DEFINITIONS if n.key == key), NOTE_DEFINITIONS[0])
        self._update_note_file_label()

    def _note_file_path(self) -> Optional[Path]:
        folder = self._current_note_folder()
        if not folder:
            return None
        return folder / self._current_note.file_name(self.support_edit.text().strip())

    def _update_note_file_label(self) -> None:
        path = self._note_file_path()
        self.note_file_label.setText(f"ファイル: {path if path else '-'}")

    def _on_note_open(self) -> None:
        if not self._current_case:
            QMessageBox.information(self, "情報", "案件を選択してください。")
            return
        path = self._note_file_path()
        if not path or not path.exists():
            QMessageBox.warning(self, "エラー", "ノートファイルが存在しません。")
            return
        try:
            if os.name == "nt":
                os.startfile(str(path))  # type: ignore[attr-defined]
            else:  # pragma: no cover
                import subprocess

                subprocess.Popen(["xdg-open", str(path)])
        except OSError as exc:
            QMessageBox.critical(self, "エラー", f"ノートを開けません: {exc}")

    def _on_note_append(self) -> None:
        if not self._current_case:
            QMessageBox.information(self, "情報", "案件を選択してください。")
            return
        body = self.note_editor.toPlainText().strip()
        if not body:
            QMessageBox.warning(self, "エラー", "追記内容を入力してください。")
            return
        try:
            append_note(
                self._current_case.folder_path,
                self._current_note,
                self._current_case.support_number,
                self.status_combo.currentText(),
                body,
            )
        except Exception as exc:  # noqa: BLE001
            QMessageBox.critical(self, "エラー", f"追記に失敗しました: {exc}")
            return
        self.note_editor.clear()
        self._current_case.last_updated = datetime.utcnow().isoformat()
        self._repository.update_case_entry(self._current_case)
        self.statusBar().showMessage("ノートへ追記しました。", 4000)

    def _on_note_subfolder(self) -> None:
        if not self._current_case:
            QMessageBox.information(self, "情報", "案件を選択してください。")
            return
        try:
            folder = create_subfolder(self._current_case.folder_path, self._current_note, self._current_case.support_number)
        except Exception as exc:  # noqa: BLE001
            QMessageBox.critical(self, "エラー", f"サブフォルダ作成に失敗しました: {exc}")
            return
        QMessageBox.information(self, "完了", f"サブフォルダを作成しました:\n{folder}")

    def _on_template_selected(self, index: int) -> None:
        if index < 0:
            return
        entry = self.template_combo.itemData(index)
        if not entry:
            return
        dialog = TemplateViewerDialog(
            entry.get("name", "テンプレート"),
            entry.get("text", ""),
            rename_callback=self._rename_template_entry,
            delete_callback=self._delete_template_entry,
            save_callback=self._update_template_entry,
            parent=self,
        )
        dialog.exec()

    def _on_template_save(self) -> None:
        content = self.note_editor.toPlainText().strip()
        if not content:
            QMessageBox.warning(self, "エラー", "テンプレートとして保存する本文を入力してください。")
            return
        name, ok = QInputDialog.getText(self, "テンプレート保存", "テンプレート名を入力してください:", text=self.note_selector.currentText())
        if not ok:
            return
        name = name.strip()
        if not name:
            QMessageBox.warning(self, "エラー", "テンプレート名を入力してください。")
            return
        self._update_template_entry(name, content)
        self._refresh_template_combo(name)
        QMessageBox.information(self, "完了", "テンプレートを保存しました。")

    # ------------------------------------------------------------------ helpers
    def _validate_required(self) -> bool:
        checks = [
            (self.company_edit, bool(self.company_edit.text().strip())),
            (self.support_edit, bool(self.support_edit.text().strip())),
            (self.status_combo, self.status_combo.currentIndex() >= 0),
        ]
        ok = True
        for widget, valid in checks:
            self._set_error(widget, not valid)
            ok = ok and valid
        if not self.base_path_edit.text().strip():
            self._set_error(self.base_path_edit, True)
            ok = False
        else:
            self._set_error(self.base_path_edit, False)
        return ok

    def _set_error(self, widget, has_error: bool) -> None:
        widget.setProperty("error", has_error)
        widget.style().unpolish(widget)
        widget.style().polish(widget)

    def _set_current_case(self, case: CaseRecord, persist: bool = True) -> None:
        self._current_case = case
        self.company_edit.setText(case.company)
        self.support_edit.setText(case.support_number)
        self._ensure_status_option(case.status or "")
        self.status_combo.setCurrentText(case.status or self.status_combo.itemText(0))
        self.date_edit.setDate(QDate.fromString(case.created_on, "yyyyMMdd"))
        if case.category:
            idx = self.category_combo.findText(case.category)
            if idx >= 0:
                self.category_combo.setCurrentIndex(idx)
        self.preview_edit.setText(case.folder_name)
        self.open_folder_button.setEnabled(True)
        self._case_cache[str(case.folder_path)] = case
        if persist:
            self._config.add_recent_case(self._settings, str(case.folder_path))
            self._load_history()
        self._ensure_case_notes(case)
        self._update_note_file_label()

    def _update_preview(self) -> None:
        try:
            preview = build_folder_name(
                self.date_edit.date().toString("yyyyMMdd"),
                self.company_edit.text(),
                self.support_edit.text(),
                self.status_combo.currentText(),
            )
            self.preview_edit.setText(preview)
        except ValueError:
            self.preview_edit.setText("(必須項目を入力してください)")

    def _on_support_changed(self) -> None:
        self._update_preview()
        self._update_note_file_label()

    def _on_theme_toggled(self, checked: bool) -> None:
        self._settings.dark_mode = checked
        self._config.save(self._settings)
        self._apply_theme(checked)

    def _apply_theme(self, dark: bool) -> None:
        app = QApplication.instance()
        if app:
            apply_theme(app, dark)

    def _ensure_status_option(self, value: str) -> None:
        clean = value.strip()
        if not clean:
            return
        options = self._settings.statuses or []
        if clean in options:
            return
        options.append(clean)
        self._settings.statuses = options
        self._config.save(self._settings)
        self._refresh_status_options(clean)

    def _on_status_add(self) -> None:
        text = self.status_combo.currentText().strip()
        if not text:
            QMessageBox.warning(self, "エラー", "追加するステータスを入力してください。")
            return
        self._ensure_status_option(text)
        self.status_combo.setCurrentText(text)
        self.statusBar().showMessage("ステータスを追加しました。", 3000)

    def _on_status_remove(self) -> None:
        options = self._settings.statuses or []
        current = self.status_combo.currentText().strip()
        if len(options) <= 1:
            QMessageBox.warning(self, "エラー", "これ以上削除できません。")
            return
        if current not in options:
            QMessageBox.warning(self, "情報", "削除対象のステータスが見つかりません。")
            return
        options = [opt for opt in options if opt != current]
        self._settings.statuses = options
        self._config.save(self._settings)
        next_value = options[0] if options else ""
        self._refresh_status_options(next_value)
        self.statusBar().showMessage("ステータスを削除しました。", 3000)

    def _get_template_by_name(self, name: str) -> Optional[dict[str, str]]:
        for entry in self._settings.note_templates or []:
            if entry.get("name") == name:
                return entry
        return None

    def _save_templates(self) -> None:
        if not self._settings.note_templates:
            self._settings.note_templates = []
        self._config.save(self._settings)
        self._refresh_template_combo()

    def _update_template_entry(self, name: str, text: str) -> None:
        templates = self._settings.note_templates or []
        for entry in templates:
            if entry.get("name") == name:
                entry["text"] = text
                break
        else:
            templates.append({"name": name, "text": text})
        self._settings.note_templates = templates
        self._config.save(self._settings)
        self._refresh_template_combo(name)
        self.statusBar().showMessage("テンプレートを更新しました。", 3000)

    def _rename_template_entry(self, old_name: str, new_name: str) -> None:
        if not new_name.strip():
            QMessageBox.warning(self, "エラー", "テンプレート名を入力してください。")
            return
        templates = self._settings.note_templates or []
        if any(entry.get("name") == new_name for entry in templates):
            QMessageBox.warning(self, "エラー", "同名のテンプレートが存在します。")
            return
        for entry in templates:
            if entry.get("name") == old_name:
                entry["name"] = new_name
                break
        self._settings.note_templates = templates
        self._config.save(self._settings)
        self._refresh_template_combo(new_name)
        self.statusBar().showMessage("テンプレート名を変更しました。", 3000)

    def _delete_template_entry(self, name: str) -> None:
        templates = self._settings.note_templates or []
        templates = [entry for entry in templates if entry.get("name") != name]
        self._settings.note_templates = templates
        self._config.save(self._settings)
        self._refresh_template_combo()
        self.statusBar().showMessage("テンプレートを削除しました。", 3000)

    def _ensure_case_notes(self, case: CaseRecord) -> None:
        if not case or not case.folder_path.exists():
            return
        for definition in NOTE_DEFINITIONS:
            ensure_note_file(case.folder_path, definition, case.support_number)

    # ------------------------------------------------------------------ Qt overrides
    def closeEvent(self, event: QCloseEvent) -> None:
        self._settings.window_geometry = list(bytes(self.saveGeometry()))
        self._config.save(self._settings)
        super().closeEvent(event)


class TemplateViewerDialog(QDialog):
    def __init__(
        self,
        name: str,
        text: str,
        rename_callback,
        delete_callback,
        save_callback,
        parent: QWidget | None = None,
    ) -> None:
        super().__init__(parent)
        self.setWindowTitle(f"テンプレート: {name}")
        self._name = name
        self._rename_callback = rename_callback
        self._delete_callback = delete_callback
        self._save_callback = save_callback
        self.resize(500, 350)

        layout = QVBoxLayout(self)
        self.text_edit = QTextEdit()
        self.text_edit.setPlainText(text)
        self.text_edit.setReadOnly(True)
        layout.addWidget(self.text_edit)

        button_row = QHBoxLayout()
        self.copy_button = QPushButton("コピー")
        self.edit_button = QPushButton("編集")
        self.rename_button = QPushButton("名前変更")
        self.delete_button = QPushButton("削除")
        self.close_button = QPushButton("閉じる")
        button_row.addWidget(self.copy_button)
        button_row.addWidget(self.edit_button)
        button_row.addWidget(self.rename_button)
        button_row.addWidget(self.delete_button)
        button_row.addStretch(1)
        button_row.addWidget(self.close_button)
        layout.addLayout(button_row)

        self.copy_button.clicked.connect(self._on_copy)
        self.edit_button.clicked.connect(self._on_edit_toggle)
        self.rename_button.clicked.connect(self._on_rename)
        self.delete_button.clicked.connect(self._on_delete)
        self.close_button.clicked.connect(self.accept)
        self._editing = False

    def _on_copy(self) -> None:
        QApplication.clipboard().setText(self.text_edit.toPlainText())

    def _on_edit_toggle(self) -> None:
        if not self._editing:
            self.text_edit.setReadOnly(False)
            self.edit_button.setText("保存")
            self._editing = True
        else:
            text = self.text_edit.toPlainText()
            if self._save_callback:
                self._save_callback(self._name, text)
            self.text_edit.setReadOnly(True)
            self.edit_button.setText("編集")
            self._editing = False

    def _on_rename(self) -> None:
        new_name, ok = QInputDialog.getText(self, "テンプレート名変更", "新しいテンプレート名を入力してください:", text=self._name)
        if not ok or not new_name.strip():
            return
        if self._rename_callback:
            self._rename_callback(self._name, new_name.strip())
            self._name = new_name.strip()
            self.setWindowTitle(f"テンプレート: {self._name}")

    def _on_delete(self) -> None:
        confirm = QMessageBox.question(
            self,
            "テンプレート削除",
            f"テンプレート「{self._name}」を削除しますか？",
        )
        if confirm == QMessageBox.Yes and self._delete_callback:
            self._delete_callback(self._name)
            self.accept()
