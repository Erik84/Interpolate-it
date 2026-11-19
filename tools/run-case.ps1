# Runs one end-to-end case: TestPattern -> engine (CLI) -> screen probe, and prints the probe's verdict.
#   tools\run-case.ps1 -Pattern '--every 4' -Engine '--factor 2' -Check '--factor 2 --expect-rate 60 --expect-hold 2'
# (PowerShell variables are case-insensitive and the script block below runs inside run-with-pattern.ps1, whose
#  $probe / $cli / $out variables it uses - so no parameter here may be called Probe, Cli or Out.)
param(
    [string]$Pattern = '--every 4',
    [string]$Engine = '--factor 2',
    [string]$Check = '',
    [int]$Warmup = 7,
    [int]$Measure = 6,
    [string]$Name = 'case',
    [switch]$Details
)

$global:InterpolatItCaseExit = 1     # set by the script block, which runs in another script's scope
$engineOptions = $Engine
$checkOptions = $Check
$caseName = $Name
$showDetails = [bool]$Details
$measureSeconds = $Measure
$warmupSeconds = $Warmup
& (Join-Path $PSScriptRoot 'run-with-pattern.ps1') -Pattern $Pattern -Seconds ($Warmup + $Measure + 10) -Command {
    $log = Join-Path $out "$caseName-pacing.csv"
    $samples = Join-Path $out "$caseName-probe.csv"
    $cliOut = Join-Path $out "$caseName-cli.txt"
    $cliErr = Join-Path $out "$caseName-cli.err.txt"
    Remove-Item $samples, $log -ErrorAction SilentlyContinue
    $engineArgs = "run --title TestPattern $engineOptions --seconds $($warmupSeconds + $measureSeconds + 2) --log `"$log`""
    $engineProcess = Start-Process -FilePath $cli -ArgumentList $engineArgs -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $cliOut -RedirectStandardError $cliErr
    Start-Sleep -Seconds $warmupSeconds
    # (No --dump-anomaly here: on a desktop that is in use, a picture of "what is on screen" can be a picture of
    #  somebody's work. Add it by hand when hunting a rendering problem.)
    $probeArgs = @('--seconds', $measureSeconds, '--csv', $samples, '--pattern-log', $patternLog) + @($checkOptions -split ' ' | Where-Object { $_ })
    $lines = & $probe @probeArgs 2>&1
    $global:InterpolatItCaseExit = $LASTEXITCODE
    $engineProcess.WaitForExit(15000) | Out-Null
    if ($showDetails) { $lines } else { $lines | Select-String 'probe  |output|hold|standstill|motion|evenness|static|anomaly|warning|INCONCLUSIVE|FAILED|  - |^OK|only|rror' }
    $status = Get-Content $cliOut | Where-Object { $_ -match '^\s*\d+s' } | Select-Object -Last 2 | Select-Object -First 1
    if ($status) { 'engine : ' + (($status -replace '^\s*\d+s\s+', '') -replace '\s+arriving\s+[\d.]+', '') }
    Get-Content $cliOut | Select-String 'stopped|error'
    Get-Content $cliErr -ErrorAction SilentlyContinue | Select-Object -First 5
    $py = Join-Path $root 'tools\export\.venv\Scripts\python.exe'
    if ((Test-Path $py) -and (Test-Path $samples)) { & $py -I (Join-Path $root 'tools\analyze_pacing.py') $samples $log | Select-String 'frame statistics' }
}
# Exit code 3 of the probe: another window was on top of the pattern, so nothing could be measured.
"case '$Name': " + $(switch ($global:InterpolatItCaseExit) { 0 { 'PASS' } 3 { 'INCONCLUSIVE' } default { 'FAIL' } })
