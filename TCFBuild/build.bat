@echo off
setlocal

set "PROJECT_DIR=%~dp0"
set "DIST_DIR=%PROJECT_DIR%dist"

where node >nul 2>nul
if errorlevel 1 (
  echo ERROR: Node.js 18 or newer is required.
  exit /b 1
)

node -e "const major=Number(process.versions.node.split('.')[0]); if (major < 18) { console.error('ERROR: Node.js 18 or newer is required. Found ' + process.versions.node); process.exit(1); }"
if errorlevel 1 exit /b 1

echo Validating JavaScript...
node --check "%PROJECT_DIR%server.mjs" || exit /b 1
for %%F in ("%PROJECT_DIR%src\*.mjs") do (
  node --check "%%~fF" || exit /b 1
)

echo Creating distribution...
if exist "%DIST_DIR%" rmdir /s /q "%DIST_DIR%"
mkdir "%DIST_DIR%\src" || exit /b 1
mkdir "%DIST_DIR%\tests" || exit /b 1

copy /y "%PROJECT_DIR%index.html" "%DIST_DIR%\index.html" >nul || exit /b 1
copy /y "%PROJECT_DIR%styles.css" "%DIST_DIR%\styles.css" >nul || exit /b 1
copy /y "%PROJECT_DIR%Logo.png" "%DIST_DIR%\Logo.png" >nul || exit /b 1
copy /y "%PROJECT_DIR%server.mjs" "%DIST_DIR%\server.mjs" >nul || exit /b 1
copy /y "%PROJECT_DIR%README.md" "%DIST_DIR%\README.md" >nul || exit /b 1
copy /y "%PROJECT_DIR%src\*.mjs" "%DIST_DIR%\src\" >nul || exit /b 1
copy /y "%PROJECT_DIR%tests\harness.html" "%DIST_DIR%\tests\harness.html" >nul || exit /b 1

echo Build completed: "%DIST_DIR%"
exit /b 0
