@echo off
setlocal
set "DIR=%~dp0"
set "MSBUILD=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"

if not exist "%MSBUILD%" (
  echo [Error] MSBuild.exe not found at: %MSBUILD%
  echo This machine appears to be missing .NET Framework 4.0.
  pause
  exit /b 1
)

echo ============================================================
echo game_perf_overlay - Building with system MSBuild (net4.0 / x64)
echo MSBuild: %MSBUILD%
echo ============================================================

"%MSBUILD%" "%DIR%game_perf_overlay.csproj" /t:Rebuild /p:Configuration=Release /nologo /v:minimal /clp:ErrorsOnly;Summary
if errorlevel 1 (
  echo.
  echo [FAIL] Build failed. See errors above.
  pause
  exit /b 1
)

echo.
echo [OK] Build complete. Output:
echo     %DIR%bin\Release\game_perf_overlay.exe
for %%I in ("%DIR%bin\Release\game_perf_overlay.exe") do echo     Size: %%~zI bytes
pause
exit /b 0
