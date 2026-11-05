# Сквозной аудит интерфейса DotShell.
#
# Зачем: smoke-тест проверяет, что сборка живая. Он не проверяет, что каждое окно
# ОТКРЫВАЕТСЯ. Случаи, когда программа стартует, а окно настроек падает с XamlParse
# или пустым прямоугольником, smoke пропускает.
#
# Скрипт запускает DotShell по очереди с флагами окон и смотрит три вещи:
#   1) процесс жив через 12 секунд;
#   2) окно с ожидаемым заголовком появилось;
#   3) в crash.log не прибавилось FATAL.
#
# Запуск:  pwsh -File scripts/ui-audit.ps1
# Быстро:  pwsh -File scripts/ui-audit.ps1 -Quick
# Код возврата: 0 — все окна открылись, 1 — есть провалы.

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
    Write-Host 'соберите: dotnet build src\DotShell.Island.Wpf\DotShell.Island.Wpf.csproj'
    exit 1
}

$log = Join-Path $env:LOCALAPPDATA 'DotShell\crash.log'

$cases = if ($Quick) {
    @(
        @{ Flag = '--settings';      Expect = 'DotShell — настройки' },
        @{ Flag = '--dashboard';     Expect = 'DotShell — обзор' },
        @{ Flag = '--control-center'; Expect = 'DotShell — центр управления' }
    )
} else {
    @(
        @{ Flag = '--settings';       Expect = 'DotShell — настройки' },
        @{ Flag = '--dashboard';      Expect = 'DotShell — обзор' },
        @{ Flag = '--control-center'; Expect = 'DotShell — центр управления' },
        @{ Flag = '--dock';           Expect = 'DotShell — док' },
        @{ Flag = '--slider-demo';    Expect = 'DotShell — стили ползунков' },
        @{ Flag = '--dot-demo';       Expect = 'DotShell — размер точки и зазор' },
        @{ Flag = '--widget=weather'; Expect = 'DotShell widget' },
        @{ Flag = '--tab=notes';      Expect = '' }
    )
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class UiProbe
{
    public delegate bool E(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(E cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int m);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// <summary>Ручка окна с нужным заголовком именно у этого процесса.</summary>
    public static IntPtr Find(int pid, string title)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            uint pp; GetWindowThreadProcessId(h, out pp);
            if (pp != pid) return true;

            var sb = new StringBuilder(256);
            GetWindowTextW(h, sb, sb.Capacity);

            if (sb.ToString() == title) { result = h; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static List<string> Titles(int pid)
    {
        var list = new List<string>();
        EnumWindows((h, l) =>
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256); GetWindowTextW(h, sb, sb.Capacity);
            if (sb.Length > 0) list.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@

function Get-FatalCount {
    if (-not (Test-Path $log)) { return 0 }
    return (Select-String -Path $log -Pattern 'FATAL' -ErrorAction SilentlyContinue | Measure-Object).Count
}

# --- юнит: самопроверка эмоций ---------------------------------------------
# Отдельный прогон без окна: рендерит все настроения и сравнивает пиксели.
$unitLog = Join-Path $env:LOCALAPPDATA 'DotShell\unit-selftest.txt'
if (Test-Path $unitLog) { Remove-Item $unitLog -Force }

& $Exe --unit-selftest | Out-Null
Start-Sleep -Seconds 2

$unitOk = (Test-Path $unitLog) -and ((Get-Content $unitLog -Raw -Encoding UTF8) -match 'UNIT SELFTEST PASS')
Write-Host ("юнит: эмоции — " + $(if ($unitOk) { 'ok' } else { 'ПРОВАЛ' })) -ForegroundColor $(if ($unitOk) { 'Gray' } else { 'Red' })

$failed = if ($unitOk) { 0 } else { 1 }
$results = @()

foreach ($case in $cases) {
    Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    $before = Get-FatalCount
    $process = Start-Process $Exe -ArgumentList $case.Flag -PassThru
    Start-Sleep -Seconds 12

    $alive = $null -ne (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)
    $titles = if ($alive) { [UiProbe]::Titles($process.Id) } else { @() }
    $joined = $titles -join ';'

    $windowOk = if ([string]::IsNullOrEmpty($case.Expect)) {
        $titles.Count -gt 0
    } else {
        $joined -like "*$($case.Expect)*"
    }

    $fatal = (Get-FatalCount) - $before

    # Центр управления проверяем не только «окно есть», но и «сеть на месте»:
    # в 1.1.0 там появились статус Wi-Fi и переключатели радио, и молчаливая
    # поломка этого блока не должна проходить аудитом.
    $contentOk = $true
    if ($case.Flag -eq '--control-center' -and $alive) {
        $element = [System.Windows.Automation.AutomationElement]::FromHandle(
            [UiProbe]::Find($process.Id, $case.Expect))

        if ($element -ne $null) {
            $found = $element.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)

            $text = ($found | ForEach-Object { $_.Current.Name }) -join ' '

            $contentOk = ($text -match 'WI-FI') -and ($text -match 'CPU')
        }
    }

    $ok = $alive -and $windowOk -and $fatal -eq 0 -and $contentOk

    if (-not $ok) { $failed++ }

    $results += [pscustomobject]@{
        Флаг = $case.Flag
        Жив = $alive
        Окно = $windowOk
        FATAL = $fatal
        Итог = if ($ok) { 'ok' } else { 'ПРОВАЛ' }
    }
}

Get-Process -Name DotShell -ErrorAction SilentlyContinue | Stop-Process -Force

$results | Format-Table -AutoSize | Out-String -Width 120 | Write-Host

if ($failed -eq 0) {
    Write-Host "UI AUDIT PASS · $($cases.Count) окон" -ForegroundColor Green
    exit 0
}

Write-Host "UI AUDIT FAILED · $failed из $($cases.Count)" -ForegroundColor Red
exit 1
