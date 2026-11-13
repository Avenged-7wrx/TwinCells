@echo off
rem PSModulePath is cleared for the child: started from a PowerShell 7 console, Windows PowerShell 5.1 would
rem otherwise inherit PowerShell 7 module paths and fail to load its own cmdlets (Get-FileHash, ...).
rem Trailing backslashes of the dropped or typed path are removed first: before the closing quote, \" would escape it
rem and swallow the arguments after it. The action also comes before the path.
setlocal
set "PSModulePath="
set "coopRoot=%~1"
:coopStrip
if not defined coopRoot goto coopRun
if not "%coopRoot:~-1%"=="\" goto coopRun
set "coopRoot=%coopRoot:~0,-1%"
goto coopStrip
:coopRun
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-Coop-Alpha2.ps1" -Action Undo -GameRoot "%coopRoot%"
set "coopUpdateExit=%ERRORLEVEL%"
pause
exit /b %coopUpdateExit%
