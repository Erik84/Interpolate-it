# The WPF application itself makes recordings of the test pattern: started on the pattern, the record hotkey's
# message posted to its window (start, stop, start, stop), the last file checked frame by frame and for its
# sound (tools\analyze_recording.py), and the application's log for anything that went wrong.
#   tools\test-app-record.ps1 [-Factor 4] [-AppPath <InterpolatIt.exe>] [-Name app-rec]
# The pattern is parked beyond the edge of the screen and nothing here looks at the screen, so this also works
# while the desktop is in use or the PC is locked. (What reaches the screen is test-app.ps1's business.)
# It found what the engine's own tools could not: the sound capture being disposed of on the window thread.
param(
    [int]$Factor = 4,
    [string]$AppPath = '',       # another InterpolatIt.exe than the build output: a published or an installed one
    [string]$Name = 'app-rec'
)

if (@(Get-Process -Name 'InterpolatIt' -ErrorAction SilentlyContinue).Count -gt 0) { 'Interpolate-it is running: close it first.'; exit 2 }

Add-Type -Namespace InterpolatItRecordTest -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
'@
Add-Type -AssemblyName System.Windows.Forms
$screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
$position = "$($screen.Right - 28),$($screen.Top + 200)"
$appFactor = $Factor
$appOverride = $AppPath
$caseName = $Name
$global:InterpolatItRecordTestExit = 1
# The beep is 10 dB softer than the pattern's own default: this runs on a PC somebody may be sitting at.
& (Join-Path $PSScriptRoot 'run-with-pattern.ps1') -Pattern '--every 4 --beep --beep-level -50' -Seconds 50 -Position $position -Command {
    $app = $(if ($appOverride) { $appOverride } else { Join-Path $root 'artifacts\bin\InterpolatIt.App\release_win-x64\InterpolatIt.exe' })
    "application: $app (version $((Get-Item $app).VersionInfo.ProductVersion))"
    $log = Join-Path $out "$caseName.log"
    $recording = Join-Path $out "$caseName.mp4"
    Remove-Item $log, $recording -ErrorAction SilentlyContinue
    $appProcess = Start-Process -FilePath $app -PassThru -ArgumentList "--start-on-title TestPattern --factor $appFactor --log `"$log`" --record `"$recording`" --no-settings --exit-after 36"
    function Send-Hotkey([int]$id) {
        $window = [InterpolatItRecordTest.Native]::FindWindowW([NullString]::Value, 'Interpolate-it')
        if ($window -eq [IntPtr]::Zero) { 'the application window was not found'; return }
        [void][InterpolatItRecordTest.Native]::PostMessageW($window, 0x0312, [IntPtr]$id, [IntPtr]::Zero)     # WM_HOTKEY
    }
    Start-Sleep -Seconds 11
    Send-Hotkey 3                                   # Ctrl+Alt+R: record ...
    Start-Sleep -Seconds 4
    Send-Hotkey 3                                   # ... stop ...
    Start-Sleep -Seconds 4
    Send-Hotkey 3                                   # ... a second recording in the same session (into the same file) ...
    Start-Sleep -Seconds 9
    Send-Hotkey 3                                   # ... and stop
    if (-not $appProcess.WaitForExit(30000)) { 'the application did not exit by itself'; Stop-Process -Id $appProcess.Id -Force }
    "exit code of the application: $($appProcess.ExitCode)"
    $text = @(Get-Content $log -Encoding UTF8)
    $text | Where-Object { $_ -match 'record|rror|xception|unhandled|   at ' } | ForEach-Object { if ($_.Length -gt 230) { $_.Substring(0, 230) } else { $_ } }
    $problems = @($text | Where-Object { $_ -match 'rror|xception|unhandled|failed|Could not' }).Count
    $recordings = @($text | Where-Object { $_ -match 'recording ended: Saved' }).Count
    "recordings finished: $recordings of 2; problems in the application's log: $problems"
    $fileGood = $false
    if (Test-Path $recording) {
        & (Join-Path $root 'tools\export\.venv\Scripts\python.exe') -I (Join-Path $root 'tools\analyze_recording.py') $recording --skip-start 0.7
        $fileGood = $LASTEXITCODE -eq 0
    }
    else { 'no file was written' }
    if ($fileGood -and $problems -eq 0 -and $recordings -eq 2 -and $appProcess.ExitCode -eq 0) { $global:InterpolatItRecordTestExit = 0 }
}
"application recording test: " + $(if ($global:InterpolatItRecordTestExit -eq 0) { 'PASS' } else { 'FAIL' })
exit $global:InterpolatItRecordTestExit
