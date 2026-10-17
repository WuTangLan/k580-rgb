@echo off
rem Starts K580 RGB in the system tray (creates a local Python environment on first run).
rem Pass --console to run it in this window instead.
cd /d "%~dp0"
if not exist .venv\Scripts\python.exe (
    echo Setting up Python environment...
    python -m venv .venv || (echo Python 3 is required: https://www.python.org/downloads/ & pause & exit /b 1)
    .venv\Scripts\python -m pip install -q -r requirements.txt || (pause & exit /b 1)
)
.venv\Scripts\python -c "import pystray" 2>nul || .venv\Scripts\python -m pip install -q -r requirements.txt
if /i "%~1"=="--console" (
    .venv\Scripts\python -m k580rgb %*
) else (
    start "" .venv\Scripts\pythonw.exe k580rgb.pyw %*
)
