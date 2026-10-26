@echo off
rem Starts Bridge Warden by running run.ps1 next to this file. Double-click it, or pass run.ps1's options:
rem   run.bat            demo profile
rem   run.bat -Edge      low-power edge profile
rem   run.bat -Port 8010
rem ExecutionPolicy Bypass applies to this one run only; it changes no system setting.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
if errorlevel 1 (
    echo.
    echo Bridge Warden stopped with an error. See the message above.
    pause
)
