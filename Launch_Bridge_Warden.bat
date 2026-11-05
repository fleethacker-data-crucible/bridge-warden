@echo off
rem Starts Bridge Warden by running src\run.ps1. Double-click it, or pass run.ps1's options:
rem   Launch_Bridge_Warden.bat            demo profile
rem   Launch_Bridge_Warden.bat -Edge      low-power edge profile
rem   Launch_Bridge_Warden.bat -Port 8010
rem ExecutionPolicy Bypass applies to this one run only; it changes no system setting.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0src\run.ps1" %*
if errorlevel 1 (
    echo.
    echo Bridge Warden stopped with an error. See the message above.
    pause
)
