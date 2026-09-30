@echo off
setlocal

cd /d "%~dp0"

set "STATE_DIR=%LOCALAPPDATA%\TCFUploader\events\d2ea01051654e6c9\b45ab9a711cbdc22"

if /i "%~1"=="--fresh" (
    if exist "%STATE_DIR%" (
        echo Clearing previous upload history and pending files...
        rmdir /s /q "%STATE_DIR%"

        if exist "%STATE_DIR%" (
            echo Failed to clear uploader state:
            echo %STATE_DIR%
            pause
            exit /b 1
        )
    )
    echo Starting with fresh upload state...
) else (
    echo Preserving previous upload history...
)

dotnet run --project ".\src\TCFUploader\TCFUploader.csproj" --configuration Release -- --folder "C:\Users\syedhu\source\repos\Dreamer\TCFComic\output" --browser-login

if errorlevel 1 (
    echo.
    echo TCFUploader exited with an error.
    pause
)
