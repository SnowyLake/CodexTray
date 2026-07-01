@echo off
setlocal
set "VERSION=%~1"
if "%VERSION%"=="" (
  set /p VERSION=Release version, for example 0.1.0: 
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Package-Release.ps1" -Version "%VERSION%" -NoPause
set "EXIT_CODE=%ERRORLEVEL%"
echo.
pause
exit /b %EXIT_CODE%
