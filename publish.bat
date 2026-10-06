@echo off
setlocal
call "%~dp0build_release.bat"
exit /b %ERRORLEVEL%
