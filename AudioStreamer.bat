@echo off
setlocal
cd /d "%~dp0"

set "PYTHON="
where py >nul 2>nul
if not errorlevel 1 (
    py -3.13 --version >nul 2>nul
    if not errorlevel 1 set "PYTHON=py -3.13"
)
if not defined PYTHON (
    where python >nul 2>nul
    if not errorlevel 1 (
        python -c "import sys; raise SystemExit(sys.version_info[:2] != (3, 13))" >nul 2>nul
        if not errorlevel 1 set "PYTHON=python"
    )
)
if not defined PYTHON (
    echo Python 3.13 was not found. Install Python 3.13 for Windows and try again.
    echo During installation, enable the Python launcher or add Python to PATH.
    pause
    exit /b 1
)

if not exist ".venv\Scripts\python.exe" (
    echo Preparing AudioStreamer for first use. This may take a few minutes.
    %PYTHON% -m venv .venv
    if errorlevel 1 goto :failed
    .venv\Scripts\python.exe -m pip install --disable-pip-version-check -r requirements.txt
    if errorlevel 1 goto :failed
)

.venv\Scripts\python.exe -c "from importlib.metadata import version; import pyaudiowpatch; raise SystemExit(version('PyAudioWPatch') != '0.2.12.8')" >nul 2>nul
if errorlevel 1 (
    echo Installing the AudioStreamer audio dependency.
    .venv\Scripts\python.exe -m pip install --disable-pip-version-check -r requirements.txt
    if errorlevel 1 goto :failed
)

.venv\Scripts\python.exe main.py
if errorlevel 1 goto :failed
exit /b 0

:failed
echo AudioStreamer stopped because startup failed. See logs\audiostreamer.log if it was created.
pause
exit /b 1
