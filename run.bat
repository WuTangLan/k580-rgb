@echo off
rem Starts the K580 RGB controller (creates a local Python environment on first run).
cd /d "%~dp0"
if not exist .venv\Scripts\python.exe (
    echo Setting up Python environment...
    python -m venv .venv || (echo Python 3 is required: https://www.python.org/downloads/ & pause & exit /b 1)
    .venv\Scripts\python -m pip install -q -r requirements.txt || (pause & exit /b 1)
)
.venv\Scripts\python -m k580rgb %*
