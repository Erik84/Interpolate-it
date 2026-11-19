# Does a browser keep rendering a video while our overlay covers its window completely?
# Chromium and Firefox stop rendering a window they consider occluded; the overlay is built so that it does not
# count (click-through layered tool window). This plays a local 60 fps clip in a throw-away browser profile,
# covers the window with the passthrough overlay for a while, and checks that frames keep arriving.
#   tools\test-browser-occlusion.ps1 -Browser chrome|edge|firefox [-Seconds 60]
param(
    [ValidateSet('chrome', 'edge', 'firefox')] [string]$Browser = 'chrome',
    [int]$Seconds = 60,
    [string]$Position = '6000,120'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$cli = Join-Path $root 'artifacts\bin\InterpolatIt.Cli\release_win-x64\InterpolatIt.Cli.exe'
$media = Join-Path $root 'artifacts\test\media'
New-Item -ItemType Directory -Force $media | Out-Null

$clip = Join-Path $media 'clip60.mp4'
if (-not (Test-Path $clip)) {
    & ffmpeg -hide_banner -loglevel error -y -f lavfi -i 'testsrc2=size=1280x720:rate=60' -t 20 -c:v libx264 -pix_fmt yuv420p -g 60 $clip
}
$page = Join-Path $media 'occlusion.html'
@'
<!doctype html><meta charset="utf-8"><title>InterpolatIt occlusion test</title>
<body style="margin:0;background:#111">
<video src="clip60.mp4" autoplay muted loop playsinline style="display:block;width:1280px;height:720px"></video>
</body>
'@ | Set-Content -Path $page -Encoding utf8
$url = 'file:///' + ($page -replace '\\', '/')

$profile = Join-Path $env:TEMP ("interpolatit-" + $Browser + "-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $profile | Out-Null
$x, $y = $Position.Split(',')
switch ($Browser) {
    'chrome' { $exe = 'C:\Program Files\Google\Chrome\Application\chrome.exe' }
    'edge' { $exe = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' }
    'firefox' { $exe = 'C:\Program Files\Mozilla Firefox\firefox.exe' }
}
if ($Browser -eq 'firefox') {
    @'
user_pref("browser.aboutwelcome.enabled", false);
user_pref("browser.startup.homepage_override.mstone", "ignore");
user_pref("browser.shell.checkDefaultBrowser", false);
user_pref("datareporting.policy.dataSubmissionEnabled", false);
user_pref("toolkit.telemetry.reportingpolicy.firstRun", false);
user_pref("media.autoplay.default", 0);
user_pref("browser.tabs.warnOnClose", false);
'@ | Set-Content -Path (Join-Path $profile 'user.js') -Encoding ascii
    $browserArgs = "-no-remote -profile `"$profile`" -width 1400 -height 900 -url `"$url`""
}
else {
    $browserArgs = "--user-data-dir=`"$profile`" --no-first-run --no-default-browser-check --disable-default-apps " +
        "--window-size=1400,900 --window-position=$x,$y --new-window `"$url`""
}

$process = Start-Process -FilePath $exe -ArgumentList $browserArgs -PassThru
try {
    Start-Sleep -Seconds 6
    "== $Browser, without overlay"
    & $cli trace --title 'InterpolatIt occlusion test' --seconds 5 | Select-String 'target|frames|interval in'

    "== $Browser, covered by the passthrough overlay for $Seconds s"
    $log = Join-Path $root "artifacts\test\occlusion-$Browser.csv"
    $lines = & $cli run --title 'InterpolatIt occlusion test' --passthrough --seconds $Seconds --log $log
    $lines | Select-String 'input|summary|stopped|error'
    $arriving = $lines | ForEach-Object { if ($_ -match '^\s*(\d+)s\s+(\w+).*arriving\s+([\d.]+)') { [pscustomobject]@{ t = [int]$Matches[1]; state = $Matches[2]; fps = [double]$Matches[3] } } }
    $steady = $arriving | Where-Object { $_.t -ge 3 }
    $min = ($steady | Measure-Object fps -Minimum).Minimum
    $avg = ($steady | Measure-Object fps -Average).Average
    $covered = ($steady | Where-Object { $_.state -ne 'Waiting' }).Count
    $rows = Import-Csv $log
    $transitions = 0; $previous = '1'
    foreach ($r in $rows) { if ($r.cloaked -eq '1' -and $previous -eq '0') { $transitions++ }; $previous = $r.cloaked }
    'frames arriving while covered: average {0:F1} fps, minimum {1:F1} fps over {2} s; covered {3} of {2} s; overlay dropped away {4} times' -f $avg, $min, $steady.Count, $covered, $transitions
    if ($min -ge 50 -and $transitions -le 1) { "RESULT ${Browser}: OK - keeps rendering under the overlay" }
    else { "RESULT ${Browser}: FAIL - rendering slowed down or stopped under the overlay" }
}
finally {
    # Browsers re-launch themselves, so the started process id is not reliable: close every process that was
    # started with this throw-away profile (and only those; the user's own browser is never touched).
    $name = Split-Path $profile -Leaf
    Get-CimInstance Win32_Process -Filter "Name='$(Split-Path $exe -Leaf)'" |
        Where-Object { $_.CommandLine -like "*$name*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 800
    Remove-Item -Recurse -Force $profile -ErrorAction SilentlyContinue
}
