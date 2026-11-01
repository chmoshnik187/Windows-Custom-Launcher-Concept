@echo off
setlocal EnableExtensions
set "LOG=%TEMP%\DotShell-install.log"
echo DotShell install %date% %time% > "%LOG%"

set "SRC=%~dp0DotShell.exe"
set "DEST=%LOCALAPPDATA%\Programs\DotShell"

if not exist "%SRC%" goto :noexe

echo [1/4] Copying files...
mkdir "%DEST%" 2>nul
copy /y "%SRC%" "%DEST%\DotShell.exe" >nul 2>>"%LOG%"
if errorlevel 1 goto :copyfail

echo [2/4] Creating Start menu shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Programs')+'\DotShell.lnk'); $s.TargetPath='%DEST%\DotShell.exe'; $s.IconLocation='%DEST%\DotShell.exe'; $s.Save()" >>"%LOG%" 2>&1

echo [3/4] Enabling autostart with Windows...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v DotShell /t REG_SZ /d "\"%DEST%\DotShell.exe\" --minimized" /f >nul 2>>"%LOG%"

echo [4/4] Starting DotShell...
start "" "%DEST%\DotShell.exe"

echo.
echo Done! The island appears at the top center of your screen.
echo   Ctrl+Shift+Space - expand / collapse
echo   Ctrl+Shift+V     - clipboard history
echo   Right-click the island - menu (autostart, settings, exit)
echo.
echo Russian instructions: KAK-USTANOVIT.txt next to this file.
pause
exit /b 0

:noexe
echo.
echo [ERROR] DotShell.exe not found next to install.bat.
echo If you ran install.bat from inside the ZIP archive:
echo extract the archive to a normal folder first, then run install.bat there.
echo.
pause
exit /b 1

:copyfail
echo.
echo [ERROR] Could not copy DotShell.exe to %DEST%
echo Usually this means an OLD DotShell is still running and locks the file.
echo Close it (right-click the island - Exit, or Task Manager), then run again.
echo Details: %LOG%
echo.
pause
exit /b 1
