@echo off
setlocal

set "APP_DIR=%~dp0"
set "ARCH=%PROCESSOR_ARCHITEW6432%"
if not defined ARCH set "ARCH=%PROCESSOR_ARCHITECTURE%"

if /I "%ARCH%"=="AMD64" (
  set "NODE_EXE=%APP_DIR%runtime\win-x64\node.exe"
) else if /I "%ARCH%"=="ARM64" (
  set "NODE_EXE=%APP_DIR%runtime\win-arm64\node.exe"
) else (
  echo ERROR: Unsupported Windows architecture "%ARCH%". TCFBuild supports AMD64 and ARM64.
  exit /b 1
)

if not exist "%NODE_EXE%" (
  echo ERROR: Embedded Node.js runtime is missing: "%NODE_EXE%"
  exit /b 1
)

"%NODE_EXE%" -e "const major=Number(process.versions.node.split('.')[0]); if (major < 18) { console.error('ERROR: Embedded Node.js 18 or newer is required. Found ' + process.versions.node); process.exit(1); }"
if errorlevel 1 exit /b 1

"%NODE_EXE%" "%APP_DIR%updater.mjs" %*
exit /b %errorlevel%
