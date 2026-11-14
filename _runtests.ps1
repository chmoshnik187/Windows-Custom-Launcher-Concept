$ErrorActionPreference='Continue'
$results = @()
function Run-Test($name, $cmd, $times) {
  $pass=0; $fail=0
  for($i=1; $i -le $times; $i++){
    try { Invoke-Expression $cmd 2>&1 | Out-Null; if($LASTEXITCODE -eq 0){$pass++} else {$fail++} }
    catch { $fail++ }
    Get-Process -Name DotShell -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
    Start-Sleep -Milliseconds 300
  }
  [pscustomobject]@{Test=$name; Runs=$times; Pass=$pass; Fail=$fail}
}

$all = @()
$all += Run-Test 'smoke' 'pwsh -File scripts/smoke.ps1' 20
$all += Run-Test 'ui-audit-quick' 'pwsh -File scripts/ui-audit.ps1 -Quick' 15
$all += Run-Test 'dock-audit' 'pwsh -File scripts/dock-audit.ps1' 10
$all += Run-Test 'widget-audit-quick' 'pwsh -File scripts/widget-audit.ps1 -Quick' 10
$all += Run-Test 'notify-audit' 'pwsh -File scripts/notify-audit.ps1' 10
$all += Run-Test 'player-audit' 'pwsh -File scripts/player-audit.ps1' 10
$all += Run-Test 'ui-audit-full' 'pwsh -File scripts/ui-audit.ps1' 5

$all | Format-Table -AutoSize | Out-String | Set-Content '_test100.txt' -Encoding UTF8
$total = ($all | Measure-Object -Property Runs -Sum).Sum
$tpass = ($all | Measure-Object -Property Pass -Sum).Sum
$tfail = ($all | Measure-Object -Property Fail -Sum).Sum
Add-Content '_test100.txt' "TOTAL runs=$total pass=$tpass fail=$tfail"
Get-Content '_test100.txt'
