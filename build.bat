@echo off
setlocal
set "DIR=%~dp0"
set "MSBUILD=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"

if not exist "%MSBUILD%" (
    echo [ERROR] .NET Framework MSBuild.exe was not found.
    exit /b 1
)

"%MSBUILD%" "%DIR%game_perf_overlay.csproj" /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:OutputPath=bin\Release\ /nologo /v:minimal
exit /b %ERRORLEVEL%
