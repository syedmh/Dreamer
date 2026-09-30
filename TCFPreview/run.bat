@echo off
setlocal

cd /d "%~dp0"
dotnet run --project ".\src\TCFPreview.WinForms\TCFPreview.WinForms.csproj" --configuration Release

if errorlevel 1 (
    echo.
    echo Failed to start TCF Photo Preview.
    pause
    exit /b 1
)
