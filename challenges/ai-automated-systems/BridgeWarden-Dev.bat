@echo off
rem Developer launcher for the C# host (edge-ops templates\launcher\ToolName-Dev.bat). Users will start it from a .lnk.
rem Visible console + pause so errors stay on screen. -ExecutionPolicy Bypass applies to this process only.
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0host\Start-BridgeWarden.ps1" %*
pause
