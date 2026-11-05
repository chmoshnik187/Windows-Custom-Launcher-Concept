# Аудит уведомлений: проверяет все четыре режима измерением.
#
# Зачем: уведомление нельзя вызвать руками «по расписанию» — оно приходит от чужих
# программ. Флаг --notify-test показывает одно уведомление через 2 секунды после старта,
# а этот скрипт по очереди ставит каждый режим и смотрит, что получилось:
#
#   capsule — остров вырос ВШИРЬ под текст (высота не меняется: это проверено опросом);
#   card    — появилось отдельное окно «DotShell — уведомление»;
#   toast   — ни окна, ни роста (системный тост рисует Windows, а не мы);
#   dot     — тишина: ни окна, ни роста.
#
# Запуск:  pwsh -File scripts/notify-audit.ps1
# Код возврата: 0 — все режимы ведут себя как обещано, 1 — есть расхождения.

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

$settings = Join-Path $env:LOCALAPPDATA 'DotShell\settings.json'

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class NotifyProbe
{
    public delegate bool E(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(E cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int m);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out REC r);
    [StructLayout(LayoutKind.Sequential)] public struct REC { public int L, T, Rg, B; }

    public sealed class Win { public string Title = ""; public int W, H, X, Y; }

    public static List<Win> All(int pid)
    {
        var list = new List<Win>();
        EnumWindows((h, l) =>
        {
            uint pp; GetWindowThreadProcessId(h, out pp);
            if (pp != pid || !IsWindowVisible(h)) return true;

            var sb = new StringBuilder(256); GetWindowTextW(h, sb, sb.Capacity);
            REC r; GetWindowRect(h, out r);

            list.Add(new Win { Title = sb.ToString(), W = r.Rg - r.L, H = r.B - r.T, X = r.L, Y = r.T });
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@

function Set-Mode([string]$mode) {
    $py = 'C:\Users\maxim\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe'
    if (-not (Test-Path $py)) { $py = 'python' }

    & $py -c @"
import json, io, os
p = os.path.join(os.environ['LOCALAPPDATA'], 'DotShell', 'settings.json')
j = json.load(io.open(p, encoding='utf-8'))
j['notifyMode'] = '$mode'
io.open(p, 'w', encoding='utf-8').write(json.dumps(j, ensure_ascii=False, indent=2))
"@ | Out-Null
}

$cases = @(
    @{ Mode = 'capsule'; Note = 'остров растёт под текст' },
    @{ Mode = 'card';    Note = 'появляется окно-карточка' },
    @{ Mode = 'toast';   Note = 'ни окна, ни роста' },
    @{ Mode = 'dot';     Note = 'тишина: ни окна, ни роста' }
)

$failed = 0
$rows = @()

foreach ($case in $cases) {
    Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900

    Set-Mode $case.Mode

    $process = Start-Process $Exe -ArgumentList '--notify-test' -PassThru
    Start-Sleep -Seconds 7   # 2 с до показа + 5 с на анимацию и измерение

    $alive = $null -ne (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)
    $wins = if ($alive) { [NotifyProbe]::All($process.Id) } else { @() }

    $island = $wins | Where-Object { $_.Title -eq 'DotShell' } | Select-Object -First 1
    $card = $wins | Where-Object { $_.Title -like '*уведомление*' } | Select-Object -First 1

    $islandWidth = if ($island) { $island.W } else { 0 }
    $islandHeight = if ($island) { $island.H } else { 0 }
    $cardFound = $null -ne $card

    $ok = switch ($case.Mode) {
        'capsule' { $alive -and $cardFound -eq $false -and $islandWidth -gt 560 }
        'card'    { $alive -and $cardFound }
        'toast'   { $alive -and -not $cardFound -and $islandWidth -le 560 }
        'dot'     { $alive -and -not $cardFound -and $islandWidth -le 560 }
        default   { $false }
    }

    if (-not $ok) { $failed++ }

    $rows += [pscustomobject]@{
        Режим = $case.Mode
        Ожидание = $case.Note
        Жив = $alive
        Карточка = $cardFound
        Ширина = $islandWidth
        Высота = $islandHeight
        Итог = if ($ok) { 'ok' } else { 'ПРОВАЛ' }
    }
}

Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force

# Возвращаем режим, с которым жили до проверки.
Set-Mode 'capsule'

$rows | Format-Table -AutoSize | Out-String -Width 120 | Write-Host

if ($failed -eq 0) {
    Write-Host "NOTIFY AUDIT PASS · $($cases.Count) режима" -ForegroundColor Green
    exit 0
}

Write-Host "NOTIFY AUDIT FAILED · $failed из $($cases.Count)" -ForegroundColor Red
exit 1
