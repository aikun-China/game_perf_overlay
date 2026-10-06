@echo off
setlocal
set "DIR=%~dp0"
set "MSBUILD=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
pushd "%DIR%"

if not exist "%MSBUILD%" (
  echo [ERROR] .NET Framework MSBuild.exe was not found.
  exit /b 1
)

"%MSBUILD%" "%DIR%game_perf_overlay.csproj" /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:OutputPath=bin\Release\game_perf_overlay\ /nologo /v:minimal
if errorlevel 1 exit /b 1

set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo [ERROR] Inno Setup 6 compiler ISCC.exe was not found.
  echo Install Inno Setup 6, then run this script again.
  exit /b 1
)

"%ISCC%" "%DIR%game_perf_overlay.iss"
if errorlevel 1 exit /b 1

echo [OK] Installer created in "%DIR%dist".
popd
