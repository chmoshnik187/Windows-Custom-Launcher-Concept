@echo off
setlocal EnableExtensions
set "LOG=%TEMP%\DotShell-install-full.log"
echo DotShell FULL install %date% %time% > "%LOG%"

set "SRC=%~dp0DotShell.exe"
set "DEST=%LOCALAPPDATA%\Programs\DotShell"

if not exist "%SRC%" goto :noexe

echo [1/5] Copying files...
mkdir "%DEST%" 2>nul
taskkill /IM DotShell.exe /F >nul 2>>"%LOG%"
copy /y "%SRC%" "%DEST%\DotShell.exe" >nul 2>>"%LOG%"
if errorlevel 1 goto :copyfail

echo [2/5] Creating Start menu shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Programs')+'\DotShell.lnk'); $s.TargetPath='%DEST%\DotShell.exe'; $s.IconLocation='%DEST%\DotShell.exe'; $s.Save()" >>"%LOG%" 2>&1

echo [3/5] Dark theme for Windows apps...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" /v AppsUseLightTheme /t REG_DWORD /d 0 /f >nul 2>>"%LOG%"
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" /v SystemUsesLightTheme /t REG_DWORD /d 0 /f >nul 2>>"%LOG%"

echo [4/5] Enabling autostart with Windows...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v DotShell /t REG_SZ /d "\"%DEST%\DotShell.exe\" --minimized" /f >nul 2>>"%LOG%"

echo [5/5] Starting DotShell in FULL style mode...
start "" "%DEST%\DotShell.exe" --style=full

echo.
echo Done! FULL style: the island PLUS dark title bars in every window.
echo   Island: Ctrl+Shift+Space, clipboard: Ctrl+Shift+V
echo   To go back to island-only: right-click island, or run DotShell.exe --style=island
echo   Everything is reversible: this changes per-user appearance settings only.
echo.
pause
exit /b 0

:noexe
echo.
echo [ERROR] DotShell.exe not found next to install-full.bat.
echo Extract the ZIP to a normal folder first, then run install-full.bat.
echo.
pause
exit /b 1

:copyfail
echo.
echo [ERROR] Could not copy DotShell.exe to %DEST%
echo An old DotShell may still be running and locking the file. Close it and retry.
echo Details: %LOG%
echo.
pause
exit /b 1
