@echo off
setlocal
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
    echo Run AudioStreamer.bat once to prepare the Python environment.
    pause
    exit /b 1
)

".venv\Scripts\python.exe" -m control_client.main
if errorlevel 1 (
    echo Device Manager stopped. Check logs\control_broker.log and the Control Plane port.
    pause
    exit /b 1
)
