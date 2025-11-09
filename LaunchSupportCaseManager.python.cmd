@echo off
setlocal
set "BASE=%~1"
if not defined BASE set "BASE=%CD%"

echo Starting Support Case Manager (PySide6 preview)...
echo Base Path: %BASE%

set "VENV_PY=%~dp0.venv\Scripts\python.exe"
set "PY_PATH=%~dp0python"
set "PYTHONPATH=%PY_PATH%;%PYTHONPATH%"

if exist "%VENV_PY%" (
    "%VENV_PY%" -m support_case_manager --base-path "%BASE%"
) else (
    py -3 -m support_case_manager --base-path "%BASE%"
)

if errorlevel 1 (
    echo.
    echo Python edition exited with an error. See SupportCaseManager.log
    echo Press any key to continue...
    pause >nul
)

