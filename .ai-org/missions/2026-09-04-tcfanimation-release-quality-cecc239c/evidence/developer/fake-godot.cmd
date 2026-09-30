@echo off
setlocal EnableExtensions EnableDelayedExpansion

if "%~1"=="--version" (
    if not defined FAKE_GODOT_VERSION set "FAKE_GODOT_VERSION=4.5.1.stable.mono.fake"
    if not defined FAKE_GODOT_VERSION_EXIT set "FAKE_GODOT_VERSION_EXIT=0"
    if defined FAKE_GODOT_VERSION echo !FAKE_GODOT_VERSION!
    exit /b !FAKE_GODOT_VERSION_EXIT!
)

set "LAST_ARGUMENT="
:collect_arguments
if "%~1"=="" goto arguments_collected
set "LAST_ARGUMENT=%~1"
shift
goto collect_arguments

:arguments_collected
if "%FAKE_GODOT_CREATE_OUTPUT%"=="1" (
    set "FAKE_GODOT_OUTPUT=!LAST_ARGUMENT!"
    powershell.exe -NoProfile -Command "[IO.File]::WriteAllText($env:FAKE_GODOT_OUTPUT, 'fake executable')"
    if errorlevel 1 exit /b 99
)

if not defined FAKE_GODOT_RUN_EXIT set "FAKE_GODOT_RUN_EXIT=0"
exit /b !FAKE_GODOT_RUN_EXIT!
