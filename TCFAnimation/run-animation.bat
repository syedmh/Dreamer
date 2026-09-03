@echo off
setlocal

set "PROJECT_DIR=%~dp0"
set "PROJECT_DIR=%PROJECT_DIR:~0,-1%"
set "BUNDLED_GODOT=%PROJECT_DIR%\.tools\godot-4.5.1-mono\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64.exe"

if exist "%BUNDLED_GODOT%" (
    set "GODOT_COMMAND=%BUNDLED_GODOT%"
    goto launch
)

if defined GODOT_EXE (
    set "GODOT_COMMAND=%GODOT_EXE%"
    goto launch
)

where.exe godot4 >nul 2>&1
if not errorlevel 1 (
    set "GODOT_COMMAND=godot4"
    goto launch
)

where.exe godot >nul 2>&1
if not errorlevel 1 (
    set "GODOT_COMMAND=godot"
    goto launch
)

echo Error: Godot Engine was not found.
echo Install Godot 4 Mono, restore the bundled .tools directory, or set GODOT_EXE.
exit /b 1

:launch
"%GODOT_COMMAND%" --path "%PROJECT_DIR%"
set "EXIT_CODE=%ERRORLEVEL%"
endlocal & exit /b %EXIT_CODE%
