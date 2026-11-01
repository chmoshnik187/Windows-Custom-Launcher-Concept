@echo off
setlocal EnableExtensions

set "DEST=%LOCALAPPDATA%\Programs\DotShell"

echo Removing DotShell...

taskkill /im DotShell.exe /f >nul 2>&1
timeout /t 1 /nobreak >nul

del /q "%DEST%\DotShell.exe" 2>nul
rmdir "%DEST%" 2>nul
del /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\DotShell.lnk" 2>nul
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v DotShell /f >nul 2>&1

echo Done. Notes and settings are kept in %LOCALAPPDATA%\DotShell
echo (delete that folder manually if you want them gone too)
pause
