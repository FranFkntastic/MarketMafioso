@echo off
setlocal
pushd "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Update-MarketMafiosoReceiver.ps1"
set "exitCode=%ERRORLEVEL%"
echo.
if not "%exitCode%"=="0" (
  echo MarketMafioso Workshop Host update exited with code %exitCode%.
  echo If Docker Desktop is not running, start it and try again.
  echo.
)
pause
popd
exit /b %exitCode%
