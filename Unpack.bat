@echo off
rem Unpacks Bridge_Warden.package next to this file into .\Bridge_Warden (runs src\Unpack.ps1).
rem   Unpack.bat                       -> .\Bridge_Warden
rem   Unpack.bat -Dest D:\apps\bw
rem Copy Unpack.bat, src\Unpack.ps1 (inside a src folder) and Bridge_Warden.package into one folder, then double-click Unpack.bat.
rem -ExecutionPolicy Bypass applies to this run only; it changes no system setting.
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0src\Unpack.ps1" %*
pause
