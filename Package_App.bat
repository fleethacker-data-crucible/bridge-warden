@echo off
rem Packages Bridge Warden into Bridge_Warden.package (runs src\Package_App.ps1).
rem   Package_App.bat                  writes Bridge_Warden.package here
rem   Package_App.bat -Out D:\x.package
rem -ExecutionPolicy Bypass applies to this run only; it changes no system setting.
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0src\Package_App.ps1" %*
pause
