@echo off
setlocal

pushd "%~dp0" || exit /b 1

set "GODOT=.tools\godot-4.5.1-mono\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe"

if not exist "%GODOT%" (
    echo BUILD_ALL_FAIL reason=godot_not_found path=%GODOT%
    popd
    exit /b 1
)

echo [1/7] Building Release solution...
dotnet build TCFAnimation.sln -c Release --nologo
if errorlevel 1 goto :fail

echo [2/7] Running deterministic controller probe...
dotnet run --project ControllerProbe\ControllerProbe.csproj -c Release --no-build
if errorlevel 1 goto :fail

echo [3/7] Importing Godot resources...
"%GODOT%" --headless --path . --import
if errorlevel 1 goto :fail

echo [4/7] Checking Godot startup...
"%GODOT%" --headless --path . --quit-after 2
if errorlevel 1 goto :fail

echo [5/7] Exporting Windows release...
"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%CD%\export-release.ps1"
if errorlevel 1 goto :fail

echo [6/7] Validating release package...
python -B FrameExtraction\validate_release.py
if errorlevel 1 goto :fail

echo [7/7] Smoke testing exported runtime...
Build\TCFAnimation.exe --headless -- --verify-runtime
if errorlevel 1 goto :fail

echo BUILD_ALL_PASS output=%CD%\Build\TCFAnimation.exe
popd
exit /b 0

:fail
set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" set "EXIT_CODE=1"
echo BUILD_ALL_FAIL exit_code=%EXIT_CODE%
popd
exit /b %EXIT_CODE%
