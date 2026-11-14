# Живая проверка дока: край, ручка, peek и возврат.
#
# Скрипт запускает настоящий DotShell с --dock и мышью проверяет геометрию:
#   1) в обычном состоянии док спрятан за краем — видна только ручка;
#   2) курсор на ручке выдвигает панель;
#   3) курсор уходит — панель снова прячется;
#   4) то же самое для правого края (dockSide=right).
#
# Проверяются физические координаты окна, поэтому заодно ловится ошибка
# пересчёта DPI: на 125% спрятанный док должен оставлять ~12 px ручки,
# а не 10 и не 8.
#
# Запуск:  pwsh -File scripts/dock-audit.ps1
# Код возврата: 0 — док ведёт себя правильно, 1 — нет.

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

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class DockProbe
{
    public delegate bool E(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(E cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int m);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    const uint LEFTDOWN = 0x0002;
    const uint LEFTUP = 0x0004;

    public static void LeftDown() => mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    public static void LeftUp() => mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    public static IntPtr Find(uint pid, string title)
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
}
'@

$settingsPath = Join-Path $env:LOCALAPPDATA 'DotShell\settings.json'
$backup = Join-Path $env:TEMP ('dsh-dock-audit-' + [Guid]::NewGuid().ToString('N') + '.json')
Copy-Item $settingsPath $backup -Force

$title = 'DotShell — док'
$failed = 0

function Stop-App {
    Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600
}

function Set-Side([string]$side) {
    $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $json | Add-Member -NotePropertyName dockSide -NotePropertyValue $side -Force
    $json.dockAutoHide = $true
    $json | ConvertTo-Json -Depth 100 | Set-Content $settingsPath -Encoding UTF8
}

function Measure-Dock([int]$procId) {
    $hwnd = [DockProbe]::Find([uint32]$procId, $title)
    if ($hwnd -eq [IntPtr]::Zero) { return $null }
    $rect = New-Object DockProbe+RECT
    if (-not [DockProbe]::GetWindowRect($hwnd, [ref]$rect)) { return $null }
    return $rect
}

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) {
        Write-Host ("  ok    " + $name + " " + $detail) -ForegroundColor Gray
    } else {
        Write-Host ("  ПРОВАЛ " + $name + " " + $detail) -ForegroundColor Red
        $script:failed++
    }
}

function Run-Side([string]$side, [int]$screenWidth) {
    Write-Host ''
    Write-Host ("== край: " + $side) -ForegroundColor Cyan

    Set-Side $side
    Stop-App

    $saved = New-Object DockProbe+POINT
    [void][DockProbe]::GetCursorPos([ref]$saved)
    [void][DockProbe]::SetCursorPos([int]($screenWidth / 2), 400)

    $process = Start-Process $Exe -ArgumentList '--dock' -PassThru

    # Ждём появления окна дока.
    $rect = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -eq (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) { break }
        $rect = Measure-Dock $process.Id
        if ($null -ne $rect) { break }
        Start-Sleep -Milliseconds 250
    }

    if ($null -eq $rect) {
        Check 'окно дока' $false 'не появилось'
        [void][DockProbe]::SetCursorPos($saved.X, $saved.Y)
        return
    }

    Start-Sleep -Milliseconds 900
    $rect = Measure-Dock $process.Id
    $script:dockPid = $process.Id

    # Ширина панели в тех же координатах, что и экран: так проверка не зависит
    # от того, отдаёт ли система физические пиксели или уже масштабированные.
    $panel = $rect.R - $rect.L
    $strip = $panel * 0.35

    if ($side -eq 'left') {
        $visible = $rect.R
        Check 'спрятан за левым краем' ($visible -gt 0 -and $visible -le $strip) "видно $visible px из $panel"
        $hiddenVisible = $visible

        # Наводим курсор на видимую ручку.
        [void][DockProbe]::SetCursorPos([Math]::Max(1, $rect.R - 2), [int](($rect.T + $rect.B) / 2))
        Start-Sleep -Milliseconds 1200
        $peek = Measure-Dock $process.Id
        Check 'выехал по наведению' (($peek.R - $hiddenVisible) -ge ($panel * 0.4)) "right $visible -> $($peek.R), видно $($peek.R) из $panel"
        Check 'панель у левого края' ($peek.L -le $strip) "left=$($peek.L)"

        # Уводим курсор — панель должна спрятаться.
        [void][DockProbe]::SetCursorPos([int]($screenWidth / 2), 400)
        Start-Sleep -Milliseconds 1200
        $hidden = Measure-Dock $process.Id
        Check 'спрятался обратно' ([Math]::Abs($hidden.R - $hiddenVisible) -le 6) "right=$($hidden.R), было $hiddenVisible"
    } else {
        $visible = $screenWidth - $rect.L
        Check 'спрятан за правым краем' ($visible -gt 0 -and $visible -le $strip) "видно $visible px из $panel"
        $hiddenVisible = $visible

        [void][DockProbe]::SetCursorPos([Math]::Min($screenWidth - 1, $rect.L + 2), [int](($rect.T + $rect.B) / 2))
        Start-Sleep -Milliseconds 1200
        $peek = Measure-Dock $process.Id
        Check 'выехал по наведению' (($screenWidth - $peek.L - $hiddenVisible) -ge ($panel * 0.4)) "left $($rect.L) -> $($peek.L), видно $($screenWidth - $peek.L) из $panel"
        Check 'панель у правого края' (($screenWidth - $peek.R) -le $strip) "right=$($peek.R)"

        [void][DockProbe]::SetCursorPos([int]($screenWidth / 2), 400)
        Start-Sleep -Milliseconds 1200
        $hidden = Measure-Dock $process.Id
        Check 'спрятался обратно' ([Math]::Abs(($screenWidth - $hidden.L) - $hiddenVisible) -le 6) "left=$($hidden.L), видно $($screenWidth - $hidden.L), было $hiddenVisible"
    }

    [void][DockProbe]::SetCursorPos($saved.X, $saved.Y)
}

function Press-At([int]$x, [int]$y) {
    [void][DockProbe]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 150
    [DockProbe]::LeftDown()
    Start-Sleep -Milliseconds 150
}

function Move-Steps([int]$x1, [int]$y1, [int]$x2, [int]$y2, [int]$steps = 12) {
    for ($i = 1; $i -le $steps; $i++) {
        $x = [int]($x1 + ($x2 - $x1) * $i / $steps)
        $y = [int]($y1 + ($y2 - $y1) * $i / $steps)
        [void][DockProbe]::SetCursorPos($x, $y)
        Start-Sleep -Milliseconds 25
    }
}

function Release-Mouse {
    Start-Sleep -Milliseconds 120
    [DockProbe]::LeftUp()
    Start-Sleep -Milliseconds 900
}

function Run-Drag([int]$screenWidth, [int]$screenHeight) {
    Write-Host ''
    Write-Host '== перетаскивание' -ForegroundColor Cyan

    # Док после левого прогона спрятан — вытаскиваем его наведением.
    $rect = Measure-Dock $script:dockPid
    if ($null -eq $rect) { Check 'окно дока для drag' $false 'не найдено'; return }

    [void][DockProbe]::SetCursorPos([Math]::Max(1, $rect.R - 2), [int](($rect.T + $rect.B) / 2))
    Start-Sleep -Milliseconds 1000
    $rect = Measure-Dock $script:dockPid
    if ($null -eq $rect) { Check 'окно дока для drag' $false 'не найдено'; return }

    # Берём верхний отступ панели: там нет ни иконки, ни кнопки «+».
    $grabX = $rect.L + [int](($rect.R - $rect.L) / 2)
    $grabY = $rect.T + 4

    Press-At $grabX $grabY

    # Сдвигаем курсор на 120 px и меряем окно, не отпуская кнопку.
    # На 125% корректный drag проходит ровно 120 px; без пересчёта DPI
    # окно уехало бы на 150 px, и проверка это поймает.
    Move-Steps $grabX $grabY ($grabX + 120) $grabY 12
    Start-Sleep -Milliseconds 350
    $mid = Measure-Dock $script:dockPid

    $dragOk = $null -ne $mid -and
              [Math]::Abs(($mid.L - $rect.L) - 120) -le 12 -and
              [Math]::Abs($mid.T - $rect.T) -le 8
    Check 'drag следует за курсором' $dragOk "left $($rect.L) -> $($mid.L), top $($rect.T) -> $($mid.T)"

    $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
    Check 'во время drag сторона не меняется' ($json.dockSide -eq 'left') "dockSide=$($json.dockSide)"

    # Доводим до правой половины экрана и отпускаем: док должен прилипнуть справа.
    Move-Steps ($grabX + 120) $grabY ([int]($screenWidth * 0.8)) $grabY 16
    Release-Mouse
    Start-Sleep -Milliseconds 700

    $json = Get-Content $settingsPath -Raw | ConvertFrom-Json
    Check 'бросок вправо меняет сторону' ($json.dockSide -eq 'right') "dockSide=$($json.dockSide)"

    $hidden = Measure-Dock $script:dockPid
    if ($null -ne $hidden) {
        $visible = $screenWidth - $hidden.L
        Check 'после броска спрятан справа' ($visible -gt 0 -and $visible -le ($screenWidth * 0.1)) "видно $visible px"
    } else {
        Check 'после броска спрятан справа' $false 'окно исчезло'
    }
}

try {
    # Ширина основного экрана в тех же координатах, в которых система отдаёт
    # GetWindowRect этому процессу (физические или уже масштабированные).
    $screenWidth = [DockProbe]::GetSystemMetrics(0)
    $screenHeight = [DockProbe]::GetSystemMetrics(1)

    Write-Host ("экран: $screenWidth x $screenHeight")
    Write-Host 'док запускается по-настоящему; курсор на время аудита двигается автоматически.'

    Run-Side 'left' $screenWidth
    Run-Drag $screenWidth $screenHeight
    Run-Side 'right' $screenWidth
}
finally {
    Stop-App
    Copy-Item $backup $settingsPath -Force
    Remove-Item $backup -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failed -eq 0) {
    Write-Host 'DOCK AUDIT PASS' -ForegroundColor Green
    exit 0
}

Write-Host ("DOCK AUDIT FAILED · " + $failed + " проверок") -ForegroundColor Red
exit 1
