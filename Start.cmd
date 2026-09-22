@echo off
setlocal
if exist "%~dp0artifacts\SingleFile\LineRename.exe" (
    start "" "%~dp0artifacts\SingleFile\LineRename.exe"
    exit /b 0
)
if not exist "%~dp0artifacts\LineRename\LineRename.exe" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1"
    if errorlevel 1 (
        pause
        exit /b 1
    )
)
start "" "%~dp0artifacts\LineRename\LineRename.exe"
