# End-to-end verification matrix: for each source cadence and multiplier, run TestPattern -> engine -> screen
# probe and check what actually reached the screen.
#   tools\run-matrix.ps1 [-Only x2-30,x4-24] [-Backend nv|dml] [-Model rife-v4.25|rife-v4.6]
param(
    [string[]]$Only = @(),
    [string]$Backend = '',
    [string]$Model = '',
    [int]$Measure = 6
)

$extra = ''
if ($Backend) { $extra += " --backend $Backend" }
if ($Model) { $extra += " --model $Model" }

# name, pattern, engine, expected pictures/s, refreshes per picture, exact factor (0 = continuous), share of generated pictures
$cases = @(
    @{ n = 'pass-30'; p = '--every 4'; e = '--passthrough'; rate = 30;  hold = 4; f = 0; share = 0;    bar = 2.0; stat = 0 },
    @{ n = 'x2-30';   p = '--every 4'; e = '--factor 2';    rate = 60;  hold = 2; f = 2; share = 0.5;  bar = 1.5; stat = 4 },
    @{ n = 'x4-30';   p = '--every 4'; e = '--factor 4';    rate = 120; hold = 1; f = 4; share = 0.75; bar = 1.5; stat = 4 },
    @{ n = 'x3-30';   p = '--every 4'; e = '--factor 3';    rate = 120; hold = 1; f = 4; share = 0.75; bar = 1.5; stat = 4 },
    @{ n = 'x2-60';   p = '--every 2'; e = '--factor 2';    rate = 120; hold = 1; f = 2; share = 0.5;  bar = 1.5; stat = 4 },
    @{ n = 'x4-24';   p = '--every 5'; e = '--factor 4';    rate = 120; hold = 1; f = 5; share = 0.8;  bar = 1.5; stat = 4 },
    @{ n = 'x2-24';   p = '--every 5'; e = '--factor 2';    rate = 60;  hold = 2; f = 0; share = -1;   bar = 2.0; stat = 4 },
    @{ n = 'x2-25';   p = '--fps 25';  e = '--factor 2';    rate = 60;  hold = 2; f = 0; share = -1;   bar = 2.0; stat = 4 },
    @{ n = 'x4-25';   p = '--fps 25';  e = '--factor 4';    rate = 120; hold = 1; f = 0; share = -1;   bar = 2.0; stat = 4 },
    # A blinking square between the content frames (a caret, a streaming client's near-copies): not content, so
    # the result must be that of the plain 30 fps case.
    @{ n = 'x2-30-blip'; p = '--every 4 --blip'; e = '--factor 2'; rate = 60; hold = 2; f = 2; share = 0.5; bar = 1.5; stat = 4 },
    # A 60 fps stream whose frames now and then come a refresh late: the output must stay even.
    @{ n = 'x2-60-late'; p = '--every 2 --jitter 0.05'; e = '--factor 2'; rate = 120; hold = 1; f = 0; share = -1; bar = 2.0; stat = 4 },
    # The source stands still for a while (9.5 s after the pattern starts = inside the measurement): what stays
    # on screen must be the last real frame.
    @{ n = 'x4-25-pause'; p = '--fps 25 --pause-at 10 --pause-for 1.5'; e = '--factor 4'; check = '--standstill-real --allow-backwards 1 --max-static 4' }
)

$results = @()
foreach ($c in $cases) {
    if ($Only.Count -gt 0 -and $Only -notcontains $c.n) { continue }
    if ($c.check) { $check = $c.check }
    else {
        $check = "--expect-rate $($c.rate) --expect-hold $($c.hold) --max-bar-error $($c.bar)"
        if ($c.f -gt 0) { $check += " --factor $($c.f)" }
        if ($c.share -ge 0) { $check += " --expect-interp $($c.share)" }
        $check += $(if ($c.stat -eq 0) { ' --static-exact' } else { " --max-static $($c.stat)" })
    }
    "===== $($c.n): pattern $($c.p), engine $($c.e)$extra"
    # The pattern sits on a desktop that may be in use: when another window covered it, measure again.
    $verdict = 'INCONCLUSIVE'
    for ($attempt = 1; $attempt -le 3 -and $verdict -eq 'INCONCLUSIVE'; $attempt++) {
        $lines = & (Join-Path $PSScriptRoot 'run-case.ps1') -Name $c.n -Pattern $c.p -Engine ($c.e + $extra) -Check $check -Measure $Measure
        $lines
        # -cmatch on the verdict itself: the case name "pass-30" also contains "pass".
        $verdict = $(if ("$($lines[-1])" -cmatch ': (PASS|FAIL|INCONCLUSIVE)$') { $Matches[1] } else { 'FAIL' })
    }
    $results += [pscustomobject]@{ Case = $c.n; Result = $verdict }
}
''
'===== summary'
$results | ForEach-Object { '{0,-10} {1}' -f $_.Case, $_.Result }
if (@($results | Where-Object Result -ne 'PASS').Count -gt 0) { exit 1 }
