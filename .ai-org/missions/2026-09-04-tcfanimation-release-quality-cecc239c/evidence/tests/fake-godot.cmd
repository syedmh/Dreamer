@echo off
if "%~1"=="--version" (
  if defined FAKE_VERSION_EXIT exit /b %FAKE_VERSION_EXIT%
  echo %FAKE_VERSION%
  exit /b 0
)
echo FIXTURE_LAUNCH_ARGS %*
if defined FAKE_LAUNCH_EXIT exit /b %FAKE_LAUNCH_EXIT%
exit /b 0
