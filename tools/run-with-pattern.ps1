# Starts the TestPattern window, runs a command against it, and closes the pattern again.
#   tools\run-with-pattern.ps1 -Pattern '--every 4' -Command { & $cli trace --title TestPattern --seconds 6 }
# Inside -Command, $cli and $probe are the paths of the CLI and the screen probe, $out the artifacts\test folder.
param(
    [string]$Pattern = '--every 4',
    [Parameter(Mandatory)] [scriptblock]$Command,
    [int]$Seconds = 30,
    [string]$Position = '',      # "X,Y"; empty = a spot that no open window is using
    [string]$Configuration = 'release_win-x64'
)

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$bin = Join-Path $root 'artifacts\bin'
$patternExe = Join-Path $bin "InterpolatIt.TestPattern\$Configuration\InterpolatIt.TestPattern.exe"
$cli = Join-Path $bin "InterpolatIt.Cli\$Configuration\InterpolatIt.Cli.exe"
$probe = Join-Path $bin "InterpolatIt.ScreenProbe\$Configuration\InterpolatIt.ScreenProbe.exe"
$out = Join-Path $root 'artifacts\test'
New-Item -ItemType Directory -Force $out | Out-Null

$patternLog = Join-Path $out 'pattern.csv'

# This desktop is in use: put the pattern where it is not in anybody's way (and where nothing will cover it).
if (-not $Position) {
    $Position = & $cli find-free-area --size 1296x760
    if ($LASTEXITCODE -ne 0 -or -not $Position) {
        $Position = '6200,150'
        'note: no free area on the screen; the test pattern will overlap open windows'
    }
}
$global:InterpolatItPatternPosition = $Position
$patternArgs = "$Pattern --seconds $Seconds --pos $Position --log `"$patternLog`""
$process = Start-Process -FilePath $patternExe -ArgumentList $patternArgs -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $out 'pattern.out.txt') -RedirectStandardError (Join-Path $out 'pattern.err.txt')
try {
    Start-Sleep -Milliseconds 1500      # window creation + refresh measurement
    & $Command
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Get-Content (Join-Path $out 'pattern.err.txt') -ErrorAction SilentlyContinue | Select-Object -First 5
}
