# Runs the engine on a test pattern that is parked almost entirely beyond the right edge of the screen: only a
# strip of a few pixels covers the desktop, yet Windows keeps composing the window, so capture, pipeline and
# presenter work as usual. For checks while somebody is using the screen.
# What reaches the screen cannot be measured this way (run-case.ps1 does that); the engine's own numbers can.
#   tools\run-offscreen.ps1 -Pattern '--every 2' -Engine '--factor 2 --enhance' [-Seconds 20] [-Name x] [-Show 'clean-up|stale']
param(
    [string]$Pattern = '--every 4',
    [string]$Engine = '--factor 2',
    [int]$Seconds = 20,
    [string]$Name = 'offscreen',
    [string]$Show = ''              # lines of the engine's trace (standard error) to print, as a regular expression
)

Add-Type -AssemblyName System.Windows.Forms
$screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
$position = "$($screen.Right - 28),$($screen.Top + 200)"
$engineOptions = $Engine
$runSeconds = $Seconds
$caseName = $Name
$showPattern = $Show
& (Join-Path $PSScriptRoot 'run-with-pattern.ps1') -Pattern $Pattern -Seconds ($Seconds + 8) -Position $position -Command {
    $cliOut = Join-Path $out "$caseName-cli.txt"
    $cliErr = Join-Path $out "$caseName-cli.err.txt"
    $log = Join-Path $out "$caseName-pacing.csv"
    Remove-Item $log -ErrorAction SilentlyContinue
    'engine started at ' + (Get-Date).ToString('HH:mm:ss.fff') + ' (its trace counts from about 0.2 s later)'
    $engineProcess = Start-Process -FilePath $cli -ArgumentList "run --title TestPattern $engineOptions --seconds $runSeconds --log `"$log`"" `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $cliOut -RedirectStandardError $cliErr
    $engineProcess.WaitForExit(($runSeconds + 30) * 1000) | Out-Null
    $lines = Get-Content $cliOut
    $lines | Where-Object { $_ -match '^\s*\d+s' } | Select-Object -Last 3
    $lines | Select-String '^(clean-up|toggle|record|summary|stopped|error)'
    if ($showPattern) { Get-Content $cliErr | Select-String $showPattern | Select-Object -First 40 }
    else { Get-Content $cliErr | Select-Object -First 6 }
}
