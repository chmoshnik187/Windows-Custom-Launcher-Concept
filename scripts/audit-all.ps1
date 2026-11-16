# Sequential regression runner for DotShell audits (D0.3).
# Runs: build -> smoke -> ui-audit (-Quick) -> widget-audit (-Quick) -> dock-audit
# Collects logs and summary into an artifacts directory.

[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [switch]$SkipBuild,
    [string]$ArtifactsDir = ''
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ArtifactsDir)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ArtifactsDir = Join-Path $env:LOCALAPPDATA "DotShell\audit-artifacts\$stamp"
}
New-Item -ItemType Directory -Force -Path $ArtifactsDir | Out-Null

$summary = [System.Collections.Generic.List[object]]::new()

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    $log = Join-Path $ArtifactsDir ("{0}.log" -f ($Name -replace '[^a-zA-Z0-9_-]','_'))
    Write-Host "== $Name =="
    $start = Get-Date

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Action 2>&1 | Tee-Object -FilePath $log
        $code = $LASTEXITCODE
    }
    catch {
        $_ | Out-String | Tee-Object -FilePath $log -Append
        $code = 1
    }
    finally {
        $ErrorActionPreference = $prevEap
    }

    if ($null -eq $code) { $code = 0 }
    $dur = [int]((Get-Date) - $start).TotalSeconds
    $status = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
    $summary.Add([pscustomobject]@{ Step = $Name; Status = $status; ExitCode = $code; Seconds = $dur; Log = $log }) | Out-Null
    Write-Host ("{0}: {1} (exit={2}, {3}s)" -f $Name, $status, $code, $dur)
    Write-Host ""
    return $code
}

if (-not $SkipBuild) {
    $sln = Join-Path $root 'DotShell.sln'
    Invoke-Step -Name 'build' -Action { & dotnet build $sln -c $Configuration --nologo; $global:LASTEXITCODE = $LASTEXITCODE } | Out-Null
}

Invoke-Step -Name 'smoke' -Action { & (Join-Path $root 'scripts\smoke.ps1'); $global:LASTEXITCODE = $LASTEXITCODE } | Out-Null
Invoke-Step -Name 'ui-audit-quick' -Action { & (Join-Path $root 'scripts\ui-audit.ps1') -Quick; $global:LASTEXITCODE = $LASTEXITCODE } | Out-Null
Invoke-Step -Name 'widget-audit-quick' -Action { & (Join-Path $root 'scripts\widget-audit.ps1') -Quick; $global:LASTEXITCODE = $LASTEXITCODE } | Out-Null
Invoke-Step -Name 'dock-audit' -Action { & (Join-Path $root 'scripts\dock-audit.ps1'); $global:LASTEXITCODE = $LASTEXITCODE } | Out-Null

$summaryPath = Join-Path $ArtifactsDir 'summary.txt'
$summary | Format-Table -AutoSize | Out-String -Width 200 | Set-Content -Path $summaryPath -Encoding UTF8
$summary | Format-Table -AutoSize
Write-Host "Artifacts: $ArtifactsDir"

$failed = ($summary | Where-Object { $_.Status -ne 'PASS' }).Count
if ($failed -gt 0) { exit 1 }
exit 0
