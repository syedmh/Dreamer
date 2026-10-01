@echo off
setlocal

set "PROJECT_DIR=%~dp0"

if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%PROJECT_DIR%package-release.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%PROJECT_DIR%package-release.ps1" -Version "%~1"
)

exit /b %errorlevel%
