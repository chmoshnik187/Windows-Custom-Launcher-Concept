# Аудит плеера: проверяет медиа-путь без живого Spotify.
#
# Флаг --player-fixture подставляет тестовый трек («Ночной дождь») в MediaBridge,
# и дальше весь UI работает с ним как с настоящим SMTC: остров, виджеты, обзор.
#
# Скрипт проверяет две вещи:
#   1) метаданные доехали — в виджете видно название и исполнителя (через UI Automation);
#   2) позиция живёт — значение прогресс-бара через RangeValuePattern растёт
#      (значит LivePosition считается, а не стоит на нуле).
#
# Раньше позиция проверялась скриншотами нижней полосы: полоса в режиме «pill»
# всего 2 px, за 4 секунды сдвигается на ~8 px, и порог «изменилось >20 пикселей»
# давал ложный провал. UIA-значение точное и не зависит от DPI/масштаба.
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
    $Exe = Join-Path $root 'src\DotShell.Island.Wpf\bin\Debug\net10.0-windows10.0.26100.0\DotShell.exe'
}

if (-not (Test-Path $Exe)) {
    Write-Host "нет exe: $Exe" -ForegroundColor Red
    exit 1
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PlayerProbe
{
    public delegate bool E(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(E cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int m);
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

Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$store = Join-Path $env:LOCALAPPDATA 'DotShell\widgets.json'
Set-Content $store '[]' -Encoding UTF8

$process = Start-Process $Exe -ArgumentList '--player-fixture', '--widget=media', '--widget-size=520x140' -PassThru

# Окно ждём циклом: на холодном старте фиксированные 15 секунд либо тормозили
# аудит, либо не хватали. Виджет создаётся после инициализации острова.
$hwnd = [IntPtr]::Zero
$deadline = [DateTime]::UtcNow.AddSeconds(20)
while ([DateTime]::UtcNow -lt $deadline) {
    if ($null -eq (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) { break }
    $hwnd = [PlayerProbe]::Find('DotShell widget')
    if ($hwnd -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 250
}

if ($hwnd -eq [IntPtr]::Zero) {
    Write-Host 'ПРОВАЛ: виджет медиа не появился' -ForegroundColor Red
    Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force
    exit 1
}

# Даём виджету наполниться: текст и прогресс-бар появляются после первого тика.
Start-Sleep -Seconds 2

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

# --- 2. позиция: значение прогресс-бара должно вырасти за 4 секунды ---
$value1 = $null
$value2 = $null
$barFound = $false

try {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ProgressBar)

    $bar = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($bar -ne $null) {
        $barFound = $true
        $range = $bar.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        $value1 = $range.Current.Value
        Start-Sleep -Seconds 4
        $value2 = $range.Current.Value
    }
} catch {
    # UIA может не отдать паттерн на холодном старте — это провал проверки,
    # а не повод падать всему аудиту.
}

$moved = ($value1 -ne $null) -and ($value2 -ne $null) -and
         ([double]$value2 - [double]$value1 -gt 0.5)

Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force

# --- отчёт ---
Write-Host ''
Write-Host ("название трека: " + $(if ($titleOk) { 'ok' } else { 'ПРОВАЛ' }))
Write-Host ("исполнитель:    " + $(if ($artistOk) { 'ok' } else { 'ПРОВАЛ' }))

if (-not $barFound) {
    Write-Host 'позиция едет:   ПРОВАЛ (прогресс-бар не найден)' -ForegroundColor Red
} elseif ($moved) {
    Write-Host ("позиция едет:   ok ({0:0.00}% → {1:0.00}%)" -f $value1, $value2)
} else {
    Write-Host ("позиция едет:   ПРОВАЛ ({0} → {1})" -f $value1, $value2) -ForegroundColor Red
}

Write-Host ''

if ($titleOk -and $artistOk -and $moved) {
    Write-Host 'PLAYER AUDIT PASS' -ForegroundColor Green
    exit 0
}

Write-Host 'PLAYER AUDIT FAILED' -ForegroundColor Red
exit 1

