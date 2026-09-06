@echo off
setlocal

set "PROJECT_DIR=%~dp0"

call "%PROJECT_DIR%build.bat"
if errorlevel 1 exit /b 1

echo Starting TCF fundraiser...
node "%PROJECT_DIR%dist\server.mjs" %*
exit /b %errorlevel%
