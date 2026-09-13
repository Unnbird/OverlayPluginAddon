@echo off

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set BUILD_EXIT=%ERRORLEVEL%
if not "%CI%"=="true" pause
exit /b %BUILD_EXIT%
