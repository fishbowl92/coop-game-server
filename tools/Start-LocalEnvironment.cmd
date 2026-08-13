@echo off
setlocal

REM Keep this CMD wrapper ASCII-only because cmd.exe depends on the Windows code page.
REM Detailed Korean guidance remains in Start-LocalEnvironment.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-LocalEnvironment.ps1"
set "ScriptExitCode=%ERRORLEVEL%"

echo.
if not "%ScriptExitCode%"=="0" (
    echo Local environment startup failed. Exit code: %ScriptExitCode%
)

pause
exit /b %ScriptExitCode%
