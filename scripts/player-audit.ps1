# Аудит плеера: проверяет медиа-путь без живого Spotify.
#
# Флаг --player-fixture подставляет тестовый трек («Ночной дождь») в MediaBridge,
# и дальше весь UI работает с ним как с настоящим SMTC: остров, виджеты, обзор.
#
# Скрипт проверяет две вещи:
#   1) метаданные доехали — в виджете видно название и исполнителя (через UI Automation);
#   2) позиция живёт — два снимка с разницей 4 секунды отличаются в зоне прогресса
#      (значит LivePosition считается, а не стоит на нуле).
#
# Запуск:  pwsh -File scripts/player-audit.ps1
# Код возврата: 0 — плеер работает, 1 — нет.

[CmdletBinding()]
param(
    [string]$Exe = ''
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $root 'src\DotShell.Island.Wpf\bin\Debug\net8.0-windows10.0.22621.0\DotShell.exe'
}

if (-not (Test-Path $Exe)) {
    Write-Host "нет exe: $Exe" -ForegroundColor Red
    exit 1
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PlayerProbe
{
    public delegate bool E(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(E cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int m);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out REC r);
    [StructLayout(LayoutKind.Sequential)] public struct REC { public int L, T, Rg, B; }
    public static IntPtr Find(string t)
    {
        IntPtr f = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            var sb = new StringBuilder(256); GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString() == t) { f = h; return false; }
            return true;
        }, IntPtr.Zero);
        return f;
    }
}
'@

$temp = Join-Path $env:TEMP 'dsh-player-audit'
if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
New-Item -ItemType Directory $temp | Out-Null

Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$store = Join-Path $env:LOCALAPPDATA 'DotShell\widgets.json'
Set-Content $store '[]' -Encoding UTF8

$process = Start-Process $Exe -ArgumentList '--player-fixture', '--widget=media', '--widget-size=520x140' -PassThru
Start-Sleep -Seconds 15

$hwnd = [PlayerProbe]::Find('DotShell widget')
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Host 'ПРОВАЛ: виджет медиа не появился' -ForegroundColor Red
    Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force
    exit 1
}

# --- 1. метаданные ---
$element = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$all = $element.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

$texts = @()
foreach ($item in $all) {
    $name = $item.Current.Name
    if ($name -and $name.Trim().Length -gt 0) { $texts += $name }
}
$joined = $texts -join ' '

$titleOk = $joined -match 'Ночной дождь'
$artistOk = $joined -match 'фикстура'

# --- 2. позиция: два снимка зоны прогресса ---
$rect = New-Object PlayerProbe+REC
[void][PlayerProbe]::GetWindowRect($hwnd, [ref]$rect)

$width = $rect.Rg - $rect.L
$height = $rect.B - $rect.T
$barHeight = [int][Math]::Min(30, [Math]::Max(12, $height * 0.18))

function Shot([string]$path) {
    $bmp = New-Object System.Drawing.Bitmap $width, $barHeight
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.L, $rect.B - $barHeight, 0, 0,
        (New-Object System.Drawing.Size $width, $barHeight))
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

$shot1 = Join-Path $temp 'progress-1.png'
$shot2 = Join-Path $temp 'progress-2.png'

Shot $shot1
Start-Sleep -Seconds 4
Shot $shot2

$py = 'C:\Users\maxim\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe'
if (-not (Test-Path $py)) { $py = 'python' }

$diff = & $py -c @"
import numpy as np
from PIL import Image
a = np.asarray(Image.open(r'$shot1').convert('RGB'), dtype=np.int16)
b = np.asarray(Image.open(r'$shot2').convert('RGB'), dtype=np.int16)
delta = np.abs(a - b).sum(axis=2)
print(int((delta > 24).sum()))
"@

$moved = $false
if ($diff -match '^\d+$') { $moved = [int]$diff -gt 20 }

Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force

# --- отчёт ---
Write-Host ''
Write-Host ("название трека: " + $(if ($titleOk) { 'ok' } else { 'ПРОВАЛ' }))
Write-Host ("исполнитель:    " + $(if ($artistOk) { 'ok' } else { 'ПРОВАЛ' }))
Write-Host ("позиция едет:   " + $(if ($moved) { "ok (изменено пикселей: $diff)" } else { "ПРОВАЛ (изменено: $diff)" }))
Write-Host ''

if ($titleOk -and $artistOk -and $moved) {
    Write-Host 'PLAYER AUDIT PASS' -ForegroundColor Green
    exit 0
}

Write-Host 'PLAYER AUDIT FAILED' -ForegroundColor Red
exit 1
