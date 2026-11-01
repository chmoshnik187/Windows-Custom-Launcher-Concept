# Smoke-тест DotShell.
#
# Зачем это вообще: каждая правка в этом проекте проверялась руками — запустить,
# посмотреть снимок, поискать ошибку в логе. Так пропускается то, что ломается молча:
# приложение стартует, но падает в фоне, версия в сборке не та, тосты не работают.
# Этот скрипт отвечает на вопрос «сборка живая?» до того, как её увидит человек.
#
# Запуск:  pwsh -File scripts/smoke.ps1
# Код возврата: 0 — всё в порядке, 1 — есть провалы (список печатается).

[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/DotShell.Island.Wpf/DotShell.Island.Wpf.csproj'
$props = Join-Path $root 'Directory.Build.props'
$log = Join-Path $env:LOCALAPPDATA 'DotShell/crash.log'

$failures = New-Object System.Collections.Generic.List[string]

function Write-Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "  ok    $text" -ForegroundColor Green }
function Write-Bad([string]$text)  { Write-Host "  FAIL  $text" -ForegroundColor Red; $failures.Add($text) }

Write-Step 'версия в проекте'
if (-not (Test-Path $props)) { Write-Bad "нет $props"; exit 1 }
$propsText = Get-Content $props -Raw -Encoding UTF8
if ($propsText -match '<Version>([^<]+)</Version>') {
    $expected = $Matches[1]
    Write-Ok "в Directory.Build.props: $expected"
} else {
    Write-Bad 'не нашёл <Version> в Directory.Build.props'
    exit 1
}

if (-not $SkipBuild) {
    Write-Step 'сборка'
    $build = & dotnet build $project -c $Configuration -v q --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Bad 'сборка не прошла'
        $build | Select-Object -Last 20 | ForEach-Object { Write-Host "    $_" }
        exit 1
    }
    Write-Ok 'сборка чистая'
}

$exe = Join-Path $root "src/DotShell.Island.Wpf/bin/$Configuration/net8.0-windows10.0.22621.0/DotShell.exe"
if (-not (Test-Path $exe)) { Write-Bad "нет exe: $exe"; exit 1 }

# Отметка в логе: всё, что появится ниже этой строки, относится к этому прогону.
$logOffset = 0
if (Test-Path $log) { $logOffset = (Get-Item $log).Length }

Write-Step 'проба версии'
$probeOut = Join-Path $env:TEMP 'dotshell-probe.txt'
$probe = Start-Process $exe -ArgumentList '--version-probe' -RedirectStandardOutput $probeOut -PassThru -Wait -NoNewWindow
$probeText = ''
if (Test-Path $probeOut) { $probeText = (Get-Content $probeOut -Raw -Encoding UTF8).Trim() }
if ($probe.ExitCode -ne 0) {
    Write-Bad "проба вернула код $($probe.ExitCode)"
} elseif ($probeText -ne $expected) {
    Write-Bad "проба сообщила «$probeText», ожидали «$expected»"
} else {
    Write-Ok "exe отвечает $probeText"
}

Write-Step 'самопроверка (--selftest)'
$report = Join-Path $env:TEMP 'dotshell-selftest.txt'
if (Test-Path $report) { Remove-Item $report -Force }
$self = Start-Process $exe -ArgumentList "--selftest=$report" -PassThru -Wait -NoNewWindow

if (-not (Test-Path $report)) {
    Write-Bad 'самопроверка не оставила отчёт'
} else {
    $lines = Get-Content $report -Encoding UTF8
    foreach ($line in $lines) {
        if ($line -like 'ok*') { Write-Ok ($line -replace '^ok\s+', '') }
        elseif ($line -like 'FAIL*') { Write-Bad ($line -replace '^FAIL\s+', '') }
    }

    if ($self.ExitCode -ne 0) { Write-Bad "самопроверка вернула код $($self.ExitCode)" }
    if ($lines -notcontains 'SMOKE ok') { Write-Bad 'в отчёте нет строки SMOKE ok' }
}

Write-Step 'лог после запуска'
if (Test-Path $log) {
    $stream = [System.IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$stream.Seek($logOffset, 'Begin')
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
        $tail = $reader.ReadToEnd()
    } finally { $stream.Dispose() }

    $fatal = @($tail -split "`r?`n" | Where-Object { $_ -match 'FATAL' })
    if ($fatal.Count -gt 0) {
        Write-Bad "в логе $($fatal.Count) записей FATAL"
        $fatal | Select-Object -First 3 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    } else {
        Write-Ok 'FATAL нет'
    }
} else {
    Write-Ok 'лог ещё не создан'
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "SMOKE FAIL: $($failures.Count) проблем(ы)" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'SMOKE PASS' -ForegroundColor Green
exit 0
