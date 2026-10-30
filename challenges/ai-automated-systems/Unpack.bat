@echo off
rem Unpacks Bridge_Warden.package next to this file into .\Bridge_Warden (runs Unpack.ps1 next to this file).
rem   Unpack.bat                       -> .\Bridge_Warden
rem   Unpack.bat -Dest D:\apps\bw
rem Copy Unpack.bat, Unpack.ps1 and Bridge_Warden.package into one folder, then double-click Unpack.bat.
rem -ExecutionPolicy Bypass applies to this run only; it changes no system setting.
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Unpack.ps1" %*
pause
