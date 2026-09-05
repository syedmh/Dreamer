@echo off
setlocal

set "RUN_MODE=%~1"
if "%RUN_MODE%"=="" set "RUN_MODE=release"

if /I not "%RUN_MODE%"=="debug" if /I not "%RUN_MODE%"=="release" (
    echo Usage: %~nx0 [debug^|release]
    exit /b 2
)

"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0run-animation.ps1" -Configuration "%RUN_MODE%"
exit /b %ERRORLEVEL%
