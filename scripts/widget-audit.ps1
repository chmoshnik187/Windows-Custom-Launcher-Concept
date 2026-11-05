# Аудит виджетов: каждый тип открывается и что-то показывает.
#
# «14 виджетов × 4 размера» раньше проверялись вручную и выборочно. Здесь — матрица:
# каждый тип запускается отдельно, проверяется окно и содержимое; у трёх представителей
# прогоняются все четыре размера (полоска / маленький / нормальный / большой).
#
# «Непустой» = есть текстовые элементы ИЛИ заметные «чернила» на снимке. Второе нужно
# потому, что часть виджетов (юниты, эквалайзер) рисуется точками и не имеет текста.
#
# Важно: окно ищем строго по PID процесса. Раньше поиск шёл по заголовку глобально,
# и аудит «находил» окно предыдущего, ещё не погасшего экземпляра — отсюда ложные провалы.
#
# Запуск:  pwsh -File scripts/widget-audit.ps1
# Быстро:  pwsh -File scripts/widget-audit.ps1 -Quick   (5 типов, один размер)
# Код возврата: 0 — всё открылось и не пустое, 1 — есть провалы.

[CmdletBinding()]
param(
    [switch]$Quick,
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
public static class WidgetProbe
{
    public delegate bool E(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(E cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int m);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out REC r);
    [StructLayout(LayoutKind.Sequential)] public struct REC { public int L, T, Rg, B; }

    /// <summary>Окно с нужным заголовком именно у этого процесса.</summary>
    public static IntPtr Find(int pid, string title)
    {
        IntPtr f = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            uint pp; GetWindowThreadProcessId(h, out pp);
            if (pp != pid) return true;

            var sb = new StringBuilder(256); GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString() == title) { f = h; return false; }
            return true;
        }, IntPtr.Zero);
        return f;
    }
}
'@

$allTypes = @(
    'clock', 'media', 'weather', 'system', 'units', 'settings', 'clean',
    'dupes', 'update', 'windows', 'audio', 'currency', 'tray', 'eq'
)

$types = if ($Quick) { $allTypes | Select-Object -First 5 } else { $allTypes }

$cases = @()
foreach ($type in $types) { $cases += @{ Type = $type; Size = '420x180' } }

if (-not $Quick) {
    $matrix = @(
        @{ Type = 'media';   Sizes = @('520x60', '300x140', '520x140', '620x260') },
        @{ Type = 'weather'; Sizes = @('520x60', '300x140', '420x180', '560x280') },
        @{ Type = 'clean';   Sizes = @('520x60', '300x140', '420x180', '420x340') }
    )

    foreach ($entry in $matrix) {
        foreach ($size in $entry.Sizes) {
            $cases += @{ Type = $entry.Type; Size = $size }
        }
    }
}

$store = Join-Path $env:LOCALAPPDATA 'DotShell\widgets.json'
$failed = 0
$rows = @()

$py = 'C:\Users\maxim\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe'
if (-not (Test-Path $py)) { $py = 'python' }

function Stop-AllShell {
    Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force

    # Ждём, пока все инстансы реально погаснут: иначе следующий старт выйдет
    # по single-instance и окно не появится.
    for ($i = 0; $i -lt 20; $i++) {
        if (-not (Get-Process -Name DotShell -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 250
    }
}

foreach ($case in $cases) {
    Stop-AllShell
    Set-Content $store '[]' -Encoding UTF8

    $process = Start-Process $Exe -ArgumentList "--widget=$($case.Type)", "--widget-size=$($case.Size)" -PassThru
    Start-Sleep -Seconds 9

    $alive = $null -ne (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)
    $hwnd = if ($alive) { [WidgetProbe]::Find($process.Id, 'DotShell widget') } else { [IntPtr]::Zero }

    $texts = 0
    $ink = 0

    if ($hwnd -ne [IntPtr]::Zero) {
        $element = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $found = $element.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)

        foreach ($item in $found) {
            $name = $item.Current.Name
            if ($name -and $name.Trim().Length -gt 0) { $texts++ }
        }

        $rect = New-Object WidgetProbe+REC
        [void][WidgetProbe]::GetWindowRect($hwnd, [ref]$rect)

        $w = $rect.Rg - $rect.L
        $h = $rect.B - $rect.T

        if ($w -gt 0 -and $h -gt 0) {
            $shot = Join-Path $env:TEMP ("dsh-widget-{0}.png" -f $case.Type)
            $bmp = New-Object System.Drawing.Bitmap $w, $h
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($rect.L, $rect.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
            $g.Dispose()
            $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()

            $inkRaw = & $py -c @"
import numpy as np
from PIL import Image
import sys
a = np.asarray(Image.open(sys.argv[1]).convert('RGB'), dtype=np.int16)
flat = a.reshape(-1, 3)
colors, counts = np.unique(flat, axis=0, return_counts=True)
bg = colors[counts.argmax()]
print(int((np.abs(flat - bg).sum(axis=1) > 45).sum()))
"@ $shot

            if ($inkRaw -match '^\d+$') { $ink = [int]$inkRaw }
        }
    }

    $ok = $alive -and ($hwnd -ne [IntPtr]::Zero) -and ($texts -ge 1 -or $ink -gt 60)
    if (-not $ok) { $failed++ }

    $rows += [pscustomobject]@{
        Виджет = $case.Type
        Размер = $case.Size
        Жив = $alive
        Окно = ($hwnd -ne [IntPtr]::Zero)
        Текстов = $texts
        Пикселей = $ink
        Итог = if ($ok) { 'ok' } else { 'ПРОВАЛ' }
    }
}

Stop-AllShell
Set-Content $store '[]' -Encoding UTF8

$rows | Format-Table -AutoSize | Out-String -Width 100 | Write-Host

if ($failed -eq 0) {
    Write-Host "WIDGET AUDIT PASS · $($cases.Count) проверок" -ForegroundColor Green
    exit 0
}

Write-Host "WIDGET AUDIT FAILED · $failed из $($cases.Count)" -ForegroundColor Red
exit 1
