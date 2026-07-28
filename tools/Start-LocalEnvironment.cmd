@echo off
REM 이 파일을 더블클릭하면 새 PowerShell 창에서 시작 스크립트를 실행합니다.
REM -ExecutionPolicy Bypass는 이 실행 한 번에만 적용되며, Windows의 전역 정책을 바꾸지 않습니다.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-LocalEnvironment.ps1"

REM 실행 결과를 읽을 수 있도록 창을 바로 닫지 않습니다.
pause
