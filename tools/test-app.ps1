# Unattended check of the WPF application: start it on the test pattern, then drive the two switches through the
# same messages the global hotkeys deliver and let the screen probe confirm what reaches the screen each time.
#   tools\test-app.ps1 [-Factor 2] [-Shots]
# (WM_HOTKEY is posted to the application's window instead of pressing Ctrl+Alt+I / Ctrl+Alt+F: no keystrokes are
#  injected into a desktop someone may be typing on. Whether the real key combinations were registered is in the log.)
param(
    [int]$Factor = 2,
    [switch]$Shots,
    [string]$AppPath = ''       # another InterpolatIt.exe than the build output: a published or an installed one
)

Add-Type -Namespace InterpolatItTest -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@
Add-Type -AssemblyName System.Drawing

function Save-ScreenRegion([int]$x, [int]$y, [int]$w, [int]$h, [string]$path) {
    $bitmap = New-Object System.Drawing.Bitmap $w, $h
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $graphics.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# Pixels in a screen region that are clearly coloured (not grey, not dark): text of the fps readout's second line.
function Count-ColouredPixels([int]$x, [int]$y, [int]$w, [int]$h) {
    $bitmap = New-Object System.Drawing.Bitmap $w, $h
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $graphics.Dispose()
    $count = 0
    for ($py = 0; $py -lt $h; $py++) {
        for ($px = 0; $px -lt $w; $px++) {
            $c = $bitmap.GetPixel($px, $py)
            $max = [Math]::Max($c.R, [Math]::Max($c.G, $c.B))
            $min = [Math]::Min($c.R, [Math]::Min($c.G, $c.B))
            if ($max -gt 150 -and $max - $min -gt 80) { $count++ }
        }
    }
    $bitmap.Dispose()
    return $count
}

$appFactor = $Factor
$takeShots = [bool]$Shots
$appOverride = $AppPath
& (Join-Path $PSScriptRoot 'run-with-pattern.ps1') -Pattern '--every 4' -Seconds 75 -Command {
    $app = $(if ($appOverride) { $appOverride } else { Join-Path $root 'artifacts\bin\InterpolatIt.App\release_win-x64\InterpolatIt.exe' })
    "application: $app"
    $log = Join-Path $out 'app.log'
    Remove-Item $log -ErrorAction SilentlyContinue
    $recording = Join-Path $out 'app-record.mp4'
    Remove-Item $recording -ErrorAction SilentlyContinue
    $appProcess = Start-Process -FilePath $app -PassThru -ArgumentList "--start-on-title TestPattern --factor $appFactor --log `"$log`" --record `"$recording`" --no-settings --exit-after 62"
    $failures = @()
    $rate = 30 * $appFactor
    $hold = [int](120 / $rate)

    function Probe([string]$what, [string[]]$expect) {
        $lines = & $probe --seconds 3 @expect 2>&1
        $ok = $LASTEXITCODE -eq 0
        "{0,-34} {1}  {2}" -f $what, $(if ($ok) { 'ok  ' } else { 'FAIL' }), (($lines | Select-String 'output|INCONCLUSIVE') -join ' / ')
        if (-not $ok) { $lines | Select-String 'FAILED|  - |hold' | ForEach-Object { "      $_" } }
        return
    }
    function Send-Hotkey([int]$id) {
        $window = [InterpolatItTest.Native]::FindWindowW([NullString]::Value, 'Interpolate-it')
        if ($window -eq [IntPtr]::Zero) { 'the application window was not found'; return }
        [void][InterpolatItTest.Native]::PostMessageW($window, 0x0312, [IntPtr]$id, [IntPtr]::Zero)     # WM_HOTKEY
    }

    Start-Sleep -Seconds 9
    Probe 'interpolating' @('--expect-rate', $rate, '--expect-hold', $hold, '--max-static', 4)

    if ($takeShots) {
        $window = [InterpolatItTest.Native]::FindWindowW([NullString]::Value, 'Interpolate-it')
        $r = New-Object InterpolatItTest.Native+RECT
        if ([InterpolatItTest.Native]::GetWindowRect($window, [ref]$r)) {
            # Without the invisible resize border around the window (it shows whatever lies behind it).
            Save-ScreenRegion ($r.Left + 8) $r.Top ($r.Right - $r.Left - 16) ($r.Bottom - $r.Top - 8) (Join-Path $out 'app-window.png')
        }
    }

    # The readout is drawn over the top-left corner of the target - where the pattern has its code strip, so the
    # probe cannot read the pattern while it shows. Its last line (the third: detected, target, actual) is
    # coloured (green / yellow / red) and nothing else in that corner is: count coloured pixels there before,
    # during and after. Three lines of 18 px plus the padding make it 66 px high and 240 px wide.
    $patternX, $patternY = $global:InterpolatItPatternPosition -split ','
    $readoutX = [int]$patternX + 7 + 16         # the window's visible frame starts 7 px right of its position
    $readoutY = [int]$patternY + 48
    $before = Count-ColouredPixels $readoutX $readoutY 240 64
    Send-Hotkey 2                                   # Ctrl+Alt+F: show the fps readout
    Start-Sleep -Milliseconds 1200
    $shown = Count-ColouredPixels $readoutX $readoutY 240 64
    if ($takeShots) { Save-ScreenRegion ($readoutX - 16) ($readoutY - 48) 420 150 (Join-Path $out 'app-readout.png') }
    Send-Hotkey 2                                   # ... and hide it again
    Start-Sleep -Milliseconds 800
    $after = Count-ColouredPixels $readoutX $readoutY 240 64
    "{0,-34} {1}  coloured pixels in the corner: {2} before, {3} shown, {4} hidden again" -f 'fps readout on / off',
        $(if ($shown -gt $before + 200 -and $after -lt $before + 50) { 'ok  ' } else { 'FAIL' }), $before, $shown, $after

    Send-Hotkey 1                                   # Ctrl+Alt+I: off
    Start-Sleep -Milliseconds 800
    Probe 'switched off (real window)' @('--expect-rate', 30, '--expect-hold', 4, '--expect-interp', 0, '--static-exact')
    Send-Hotkey 1                                   # ... and on again
    Start-Sleep -Milliseconds 2500
    Probe 'switched on again' @('--expect-rate', $rate, '--expect-hold', $hold, '--max-static', 4)

    # Clean-up of compressed video: whether the graphics driver really does it is in the application's status
    # line a few seconds later; what reaches the screen must stay as regular as without it.
    Send-Hotkey 4                                   # Ctrl+Alt+H: clean-up on
    Start-Sleep -Seconds 5
    $cleanLine = "$(Get-Content $log -Encoding UTF8 | Where-Object { $_ -match '\|\| ' } | Select-Object -Last 1)"
    if ($cleanLine -match 'clean-up ([\d.]+) ms at (\d+x\d+)') { "{0,-34} ok    active, {1} ms per source frame at {2}" -f 'clean-up by the driver', $Matches[1], $Matches[2] }
    elseif ($cleanLine -match 'clean-up not active: (.*)$') { "{0,-34} FAIL  not active: {1}" -f 'clean-up by the driver', $Matches[1] }
    else { "{0,-34} FAIL  the status line says nothing about it" -f 'clean-up by the driver' }
    # (The clean-up changes a static fine pattern by up to 9/255 while something moves close to it.)
    Probe 'with clean-up' @('--expect-rate', $rate, '--expect-hold', $hold, '--max-static', 12)
    Send-Hotkey 4                                   # ... and off again
    Start-Sleep -Milliseconds 1500

    Send-Hotkey 3                                   # Ctrl+Alt+R: record ...
    Start-Sleep -Seconds 6
    Send-Hotkey 3                                   # ... and stop
    Start-Sleep -Seconds 3
    $ffprobe = "$((Get-Command ffprobe -ErrorAction SilentlyContinue | Select-Object -First 1).Source)"
    if (-not $ffprobe) { $ffprobe = 'C:\Tools\FFMPEG\ffprobe.exe' }       # where it lives on the PC this was written on
    if (-not (Test-Path $recording)) { "{0,-34} FAIL  no file was written" -f 'recording' }
    elseif (Test-Path $ffprobe) {
        $info = & $ffprobe -v error -select_streams v:0 -count_frames -show_entries stream=codec_name,width,height,nb_read_frames,duration -of csv=p=0 $recording 2>&1
        $codec, $w, $h, $duration, $frames = "$info" -split ','
        $fps = if ([double]$duration -gt 0) { [double]$frames / [double]$duration } else { 0 }
        "{0,-34} {1}  {2} {3}x{4}, {5} frames in {6:F2} s = {7:F1} fps" -f 'recording', $(if ([math]::Abs($fps - $rate) -lt 0.05 * $rate) { 'ok  ' } else { 'FAIL' }), $codec, $w, $h, $frames, [double]$duration, $fps
    }
    else { "{0,-34} ?     {1:N0} bytes written (no ffprobe to look inside)" -f 'recording', (Get-Item $recording).Length }

    if (-not $appProcess.WaitForExit(40000)) { 'the application did not exit by itself'; Stop-Process -Id $appProcess.Id -Force }
    "exit code of the application: $($appProcess.ExitCode)"
    '--- application log (without the status line of every second)'
    Get-Content $log -Encoding UTF8 | Where-Object { $_ -notmatch '\| source ' } | ForEach-Object { if ($_.Length -gt 170) { $_.Substring(0, 170) } else { $_ } }
    '--- last status line'
    Get-Content $log -Encoding UTF8 | Where-Object { $_ -match '\|\| ' } | Select-Object -Last 1
}
