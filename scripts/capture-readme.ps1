# Пересъёмка скриншотов для README.
#
# Запускает DotShell в нужных режимах и сохраняет снимки окон в images/.
# Делает бэкап пользовательских настроек/виджетов/закрепов и возвращает их после.
#
# Снимки делаются через PrintWindow(PW_RENDERFULLCONTENT): так попадают DWM-окна
# без захвата чужого рабочего стола.
#
# Запуск: pwsh -File scripts/capture-readme.ps1
# Быстро: pwsh -File scripts/capture-readme.ps1 -Only dock,dashboard

[CmdletBinding()]
param(
    [string]$Exe = '',
    [string]$Only = ''
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$images = Join-Path $root 'images'

if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $root 'src\DotShell.Island.Wpf\bin\Debug\net10.0-windows10.0.26100.0\DotShell.exe'
}
if (-not (Test-Path $Exe)) {
    Write-Host "нет exe: $Exe" -ForegroundColor Red
    exit 1
}

$wanted = @()
if (-not [string]::IsNullOrWhiteSpace($Only)) {
    $wanted = $Only.Split(',') | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ }
}
function Want([string]$name) { $wanted.Count -eq 0 -or $wanted -contains $name.ToLowerInvariant() }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class CapProbe
{
    public delegate bool E(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(E cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int m);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static IntPtr FindByPidTitle(uint pid, string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var sb = new StringBuilder(256); GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr FindByPidVisible(uint pid)
    {
        // Капсула острова называется "DotShell".
        return FindByPidTitle(pid, "DotShell");
    }
}
'@

$dir = Join-Path $env:LOCALAPPDATA 'DotShell'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$bak = Join-Path $dir "_pre-capture-$stamp"
New-Item -ItemType Directory -Path $bak -Force | Out-Null
foreach ($f in @('settings.json','widgets.json','pinned.json')) {
    $p = Join-Path $dir $f
    if (Test-Path $p) { Copy-Item $p (Join-Path $bak $f) -Force }
}

function Stop-App {
    Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700
}

function Save-Settings($obj) {
    $path = Join-Path $dir 'settings.json'
    $obj | ConvertTo-Json -Depth 100 | Set-Content $path -Encoding UTF8
}

function Load-Settings {
    $path = Join-Path $dir 'settings.json'
    if (Test-Path $path) { return Get-Content $path -Raw | ConvertFrom-Json }
    return [pscustomobject]@{}
}

function Wait-Window([int]$procId, [string]$windowTitle, [int]$sec = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($sec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -eq (Get-Process -Id $procId -ErrorAction SilentlyContinue)) { return [IntPtr]::Zero }
        $h = [CapProbe]::FindByPidTitle([uint32]$procId, $windowTitle)
        if ($h -ne [IntPtr]::Zero) { return $h }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}

function Capture-Hwnd([IntPtr]$hwnd, [string]$file, [int]$pad = 0) {
    if ($hwnd -eq [IntPtr]::Zero) { Write-Host "  skip: нет окна" -ForegroundColor Yellow; return $false }
    $rect = New-Object CapProbe+RECT
    [void][CapProbe]::GetWindowRect($hwnd, [ref]$rect)
    $w = [Math]::Max(1, $rect.R - $rect.L + $pad * 2)
    $h = [Math]::Max(1, $rect.B - $rect.T + $pad * 2)

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    try {
        # PW_RENDERFULLCONTENT = 2
        [void][CapProbe]::PrintWindow($hwnd, $hdc, 2)
    } finally {
        $g.ReleaseHdc($hdc)
        $g.Dispose()
    }
    $path = Join-Path $images $file
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  saved: images/$file ($w x $h)" -ForegroundColor Green
    return $true
}

function Capture-ProcessWindow([string[]]$argsList, [string]$windowTitle, [string]$file, [int]$waitSec = 20, [int]$extraSleep = 2) {
    Stop-App
    $p = Start-Process $Exe -ArgumentList $argsList -PassThru
    if ($null -eq $p) { Write-Host "  ПРОВАЛ: не удалось запустить $Exe" -ForegroundColor Red; return }
    $hwnd = Wait-Window -procId $p.Id -windowTitle $windowTitle -sec $waitSec
    if ($hwnd -ne [IntPtr]::Zero) {
        Start-Sleep -Seconds $extraSleep
        [void][CapProbe]::SetWindowPos($hwnd, [CapProbe]::HWND_TOPMOST, 0,0,0,0, ([CapProbe]::SWP_NOMOVE -bor [CapProbe]::SWP_NOSIZE -bor [CapProbe]::SWP_SHOWWINDOW))
        [void][CapProbe]::SetForegroundWindow($hwnd)
        Start-Sleep -Milliseconds 500
        Capture-Hwnd $hwnd $file | Out-Null
        [void][CapProbe]::SetWindowPos($hwnd, [CapProbe]::HWND_NOTOPMOST, 0,0,0,0, ([CapProbe]::SWP_NOMOVE -bor [CapProbe]::SWP_NOSIZE))
    } else {
        Write-Host "  ПРОВАЛ: окно '$windowTitle' не появилось" -ForegroundColor Red
    }
    Stop-App
}

$captured = 0
try {
    if (Want 'dashboard') {
        Write-Host '== dashboard' -ForegroundColor Cyan
        $s = Load-Settings; $s | Add-Member -NotePropertyName dockEnabled -NotePropertyValue $false -Force; Save-Settings $s
        Capture-ProcessWindow @('--dashboard') 'DotShell — обзор' 'dashboard.png' 20 3
        $captured++
    }

    if (Want 'control-center') {
        Write-Host '== control-center' -ForegroundColor Cyan
        $s = Load-Settings; $s | Add-Member -NotePropertyName dockEnabled -NotePropertyValue $false -Force; Save-Settings $s
        Capture-ProcessWindow @('--control-center') 'DotShell — центр управления' 'control-center.png' 20 3
        $captured++
    }

    if (Want 'dock') {
        Write-Host '== dock (left, visible)' -ForegroundColor Cyan
        $s = Load-Settings
        $s | Add-Member -NotePropertyName dockEnabled -NotePropertyValue $true -Force
        $s | Add-Member -NotePropertyName dockAutoHide -NotePropertyValue $false -Force
        $s | Add-Member -NotePropertyName dockSide -NotePropertyValue 'left' -Force
        Save-Settings $s
        Capture-ProcessWindow @('--dock') 'DotShell — док' 'dock-left.png' 20 3
        $captured++
    }

    if (Want 'island') {
        Write-Host '== island (home tab)' -ForegroundColor Cyan
        $s = Load-Settings
        $s | Add-Member -NotePropertyName dockEnabled -NotePropertyValue $false -Force
        Save-Settings $s
        Stop-App
        $p = Start-Process $Exe -ArgumentList @('--tab=home') -PassThru
        $hwnd = if ($p) { Wait-Window -procId $p.Id -windowTitle 'DotShell' -sec 20 } else { [IntPtr]::Zero }
        if ($hwnd -ne [IntPtr]::Zero) {
            Start-Sleep -Seconds 3
            [void][CapProbe]::SetForegroundWindow($hwnd)
            Start-Sleep -Milliseconds 500
            Capture-Hwnd $hwnd 'island-home.png' | Out-Null
        } else {
            Write-Host "  ПРОВАЛ: окно 'DotShell' не появилось" -ForegroundColor Red
        }
        Stop-App
        $captured++
    }

    if (Want 'settings') {
        Write-Host '== settings' -ForegroundColor Cyan
        $s = Load-Settings; $s | Add-Member -NotePropertyName dockEnabled -NotePropertyValue $false -Force; $s.interfaceScale = 1.0; Save-Settings $s
        Capture-ProcessWindow @('--settings') 'DotShell — настройки' 'settings-search.png' 20 3
        $captured++
    }

    if (Want 'ui-scale') {
        Write-Host '== ui-scale (interface 1.3)' -ForegroundColor Cyan
        $s = Load-Settings; $s | Add-Member -NotePropertyName dockEnabled -NotePropertyValue $false -Force; $s.interfaceScale = 1.3; Save-Settings $s
        Capture-ProcessWindow @('--settings') 'DotShell — настройки' 'ui-scale.png' 20 3
        $captured++
    }

    Write-Host ''
    Write-Host "готово: $captured сцен(ы)" -ForegroundColor Green
}
finally {
    Stop-App
    foreach ($f in @('settings.json','widgets.json','pinned.json')) {
        $src = Join-Path $bak $f
        $dst = Join-Path $dir $f
        if (Test-Path $src) { Copy-Item $src $dst -Force }
    }
    Write-Host "настройки восстановлены из $bak"
}

