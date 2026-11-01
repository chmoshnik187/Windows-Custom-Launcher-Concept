@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  DotShell one-command build.
REM
REM    build.bat              - Debug build (for development)
REM    build.bat publish      - Release single-file build into publish\
REM    build.bat installer    - publish + Inno Setup installer
REM
REM  Visual Studio is NOT required - free .NET SDK 8 is enough:
REM    winget install Microsoft.DotNet.SDK.8
REM
REM  "publish" produces a SINGLE file publish\DotShell.exe (~75 MB) that runs
REM  on any Windows 10/11 x64 without installing .NET.
REM
REM  NOTE: keep this file ASCII-only and CRLF. Non-ASCII bytes plus
REM  "chcp 65001" desync cmd.exe line parsing and silently kill batch scripts.
REM ===========================================================================

cd /d "%~dp0"

if "%~1"=="" goto :build
if /i "%~1"=="build" goto :build
if /i "%~1"=="publish" goto :publish
if /i "%~1"=="installer" goto :installer
echo Unknown mode: %~1   (build ^| publish ^| installer)
exit /b 1

:build
echo.
echo === Building Debug (x64) ===
dotnet build DotShell.sln -c Debug -p:Platform=x64
if errorlevel 1 goto :error
echo.
echo Done. Run with:
echo   dotnet run --project src\DotShell.Island.Wpf\DotShell.Island.Wpf.csproj -c Debug
goto :end

:publish
echo.
echo === Publishing Release (self-contained, single file) to publish\ ===
if exist publish rmdir /s /q publish
dotnet publish src\DotShell.Island.Wpf\DotShell.Island.Wpf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if errorlevel 1 goto :error
del /q publish\*.pdb 2>nul
echo.
echo Done: publish\DotShell.exe
echo This single file runs anywhere on Windows 10/11 x64 without .NET installed.
goto :end

:installer
call :publish
if errorlevel 1 goto :end

set ISCC=
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

if "%ISCC%"=="" (
  echo.
  echo [!] Inno Setup 6 not found.
  echo     Get it free: https://jrsoftware.org/isdl.php
  echo     Then run again: build.bat installer
  echo.
  echo     publish\ is ready anyway - run publish\DotShell.exe directly.
  goto :end
)

echo.
echo === Building installer ===
"%ISCC%" installer\DotShell.iss
if errorlevel 1 goto :error
echo.
echo Done: installer\Output\DotShell-Setup-0.5.0.exe
goto :end

:error
echo.
echo [X] Build failed.
echo     See docs\TROUBLESHOOTING.md
exit /b 1

:end
endlocal
