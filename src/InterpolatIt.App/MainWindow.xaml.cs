using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using InterpolatIt.Core.Capture;
using InterpolatIt.Core.Native;
using InterpolatIt.Core.Pipeline;

namespace InterpolatIt.App;

/// <summary>Pick a window, press Start Interpolating. The engine lives in InterpolatIt.Core.</summary>
public partial class MainWindow : Window
{
    private const uint VkF = 0x46, VkH = 0x48, VkI = 0x49, VkR = 0x52;

    private sealed record WindowItem(WindowInfo Info)
    {
        public string Label => string.IsNullOrEmpty(Info.ProcessName) ? Info.Title : $"{Info.Title}  —  {Info.ProcessName}";
    }

    private readonly Settings _settings;
    private readonly DispatcherTimer _timer;
    private readonly string _defaultHint;
    private readonly StartupOptions _startup = App.Options;
    private readonly DateTime _started = DateTime.UtcNow;
    private InterpolationSession? _session;
    private HotkeyService? _hotkeys;
    private nint _target;
    private bool _loading = true, _busy, _unattended;
    private DateTime _nextLogLine;
    private int _sessionNumber;
    private string _recordIdleText = "";
    private string? _recordResult;

    public MainWindow()
    {
        InitializeComponent();
        _defaultHint = HintText.Text;
        _recordIdleText = RecordText.Text;
        _settings = _startup.NoSettings ? new Settings() : SettingsStore.Load();
        // Overrides from the command line apply to this run only: an unattended check must not change what the
        // user had chosen.
        _unattended = _startup.NoSettings || _startup.Factor != 0 || _startup.Model is not null || _startup.Enhance;
        int factor = _startup.Factor is >= 2 and <= 4 ? _startup.Factor : _settings.Factor;
        (factor switch { 3 => Factor3, 4 => Factor4, _ => Factor2 }).IsChecked = true;
        QualityList.SelectedIndex = (_startup.Model ?? _settings.Model) == "rife-v4.6" ? 1 : 0;
        FpsSwitch.IsChecked = _settings.ShowFps;
        EnhanceSwitch.IsChecked = _settings.Enhance || _startup.Enhance;
        RefreshWindows(0);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => OnTimer(), Dispatcher);
        _loading = false;
        UpdateStatus();
        if (_startup.RenderWindowsTo is string folder)
        {
            // Far outside any screen: laid out and drawn, but never seen.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Top = -30000;
            ShowInTaskbar = false;
            ShowActivated = false;
            Loaded += (_, _) => Dispatcher.BeginInvoke(() => RenderWindows(folder), DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Draws this window and the About window into PNG files (see <c>--render-windows</c>) and exits.</summary>
    private void RenderWindows(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            WindowList.ItemsSource = null;          // titles of other people's windows have no place in a picture file
            EnhanceSwitch.IsChecked = true;         // so that its status line is part of the picture
            EnhanceText.Visibility = Visibility.Visible;
            EnhanceText.Text = "Active  ·  5.1 ms per source frame";
            UpdateLayout();
            SaveAsPng(this, Path.Combine(folder, "main-window.png"));
            var about = new AboutWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowActivated = false,
            };
            about.Show();
            about.UpdateLayout();
            SaveAsPng(about, Path.Combine(folder, "about-window.png"));
            about.Close();
        }
        finally
        {
            Close();
        }
    }

    private static void SaveAsPng(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(window.ActualWidth), height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        // The window's own background first (the content is transparent where nothing is drawn).
        var backdrop = new System.Windows.Media.DrawingVisual();
        using (var context = backdrop.RenderOpen())
            context.DrawRectangle(window.Background ?? System.Windows.Media.Brushes.White, null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    // ------------------------------------------------------------------ hotkeys

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hotkeys = new HotkeyService(this);
        uint modifiers = HotkeyService.Control | HotkeyService.Alt;
        bool onOff = _hotkeys.Register(modifiers, VkI, OnToggleHotkey);
        bool readout = _hotkeys.Register(modifiers, VkF, () => FpsSwitch.IsChecked = FpsSwitch.IsChecked != true);
        bool record = _hotkeys.Register(modifiers, VkR, () => _ = ToggleRecordingAsync());
        // H and not E for "enhance": Ctrl+Alt is AltGr, and AltGr+E types é or € on the keyboard layouts used here.
        bool cleanUp = _hotkeys.Register(modifiers, VkH, () => EnhanceSwitch.IsChecked = EnhanceSwitch.IsChecked != true);
        if (!onOff) EnabledHotkeyText.Text = "   (Ctrl+Alt+I is in use by another app)";
        if (!readout) FpsHotkeyText.Text = "   (Ctrl+Alt+F is in use by another app)";
        if (!cleanUp) EnhanceHotkeyText.Text = "   (Ctrl+Alt+H is in use by another app)";
        if (!record) _recordIdleText = _recordIdleText.Replace("Ctrl+Alt+R", "(Ctrl+Alt+R is in use by another app)");
        RecordText.Text = _recordIdleText;
        Log($"hotkeys: Ctrl+Alt+I {(onOff ? "registered" : "NOT available")}, Ctrl+Alt+F {(readout ? "registered" : "NOT available")}, " +
            $"Ctrl+Alt+R {(record ? "registered" : "NOT available")}, Ctrl+Alt+H {(cleanUp ? "registered" : "NOT available")}");

        if (_startup.StartOnTitle is string title)
        {
            var match = WindowEnumerator.FindByTitle(title);
            if (match is null) Log($"no window with '{title}' in its title");
            else
            {
                RefreshWindows(match.Handle);
                _ = StartAsync(match.Handle);
            }
        }
        if (_startup.Unattended) Dispatcher.BeginInvoke(SelfCheckControls, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Unattended runs cannot click. This does what a click on a drop-down arrow does (the toggle inside the
    /// control's template, whose two-way binding once threw "Cannot find non-neutral culture" on every click)
    /// and logs the outcome.
    /// </summary>
    private void SelfCheckControls()
    {
        try
        {
            foreach (ComboBox box in new[] { QualityList, WindowList })
            {
                if (FindDescendant<System.Windows.Controls.Primitives.ToggleButton>(box) is not { } arrow)
                {
                    Log($"self-check: no drop-down arrow found in {box.Name}");
                    continue;
                }
                var toggle = (System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(arrow);
                bool wasEnabled = box.IsEnabled;
                box.IsEnabled = true;
                toggle.Toggle();            // opens the list ...
                toggle.Toggle();            // ... and closes it again
                box.IsEnabled = wasEnabled;
            }
            Log("self-check: drop-down lists open and close");
        }
        catch (Exception e)
        {
            Log($"self-check FAILED: {e.GetType().Name}: {e.Message}");
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Appends a line to the file given with --log (nothing otherwise).</summary>
    private void Log(string line)
    {
        if (_startup.LogPath is not string path) return;
        try { File.AppendAllText(path, $"{(DateTime.UtcNow - _started).TotalSeconds,6:F1}s {line}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    private void OnTimer()
    {
        UpdateStatus();
        DateTime now = DateTime.UtcNow;
        if (_startup.LogPath is not null && now >= _nextLogLine)
        {
            _nextLogLine = now.AddSeconds(1);
            Log($"{StateText.Text} | source {SourceFpsText.Text} target {TargetFpsText.Text} actual {ActualFpsText.Text} | " +
                $"on={EnabledSwitch.IsChecked} fps-readout={FpsSwitch.IsChecked} clean-up={EnhanceSwitch.IsChecked} | {DetailText.Text}" +
                (_session is { } running ? " || " + running.Stats.Diagnostics() : ""));
        }
        if (_startup.ExitAfter > 0 && (now - _started).TotalSeconds >= _startup.ExitAfter) Close();
    }

    /// <summary>Ctrl+Alt+I: on/off while a session runs; otherwise start on the window the user is looking at.</summary>
    private void OnToggleHotkey()
    {
        if (_session is not null)
        {
            EnabledSwitch.IsChecked = EnabledSwitch.IsChecked != true;
            return;
        }
        if (_busy) return;
        nint foreground = User32.GetAncestor(User32.GetForegroundWindow(), User32.GA_ROOT);
        if (foreground == 0 || !WindowEnumerator.IsCapturable(foreground)) return;
        User32.GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == (uint)Environment.ProcessId) return;
        RefreshWindows(foreground);
        EnabledSwitch.IsChecked = true;
        _ = StartAsync(foreground);
    }

    // ------------------------------------------------------------------ recording

    private async void Record_Click(object sender, RoutedEventArgs e) => await ToggleRecordingAsync();

    /// <summary>Record button and Ctrl+Alt+R: start a recording, or finish the one that is running.</summary>
    private async Task ToggleRecordingAsync()
    {
        var session = _session;
        if (session is null)
        {
            HintText.Text = "Start interpolating first; the recording is of what the window then shows.";
            return;
        }
        if (session.RecordingState is { Finished: false })
        {
            RecordButton.IsEnabled = false;
            var done = await Task.Run(session.StopRecording);       // writing the file out takes a moment
            ShowRecordingResult(done);
            UpdateStatus();
            return;
        }
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Interpolate-it");
            string path = _startup.RecordPath ?? Path.Combine(folder, $"Interpolate-it {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            var status = session.StartRecording(path);
            _recordResult = null;
            Log($"recording started: {status.Width}x{status.Height}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or COMException)
        {
            _recordResult = "Could not record: " + ex.Message;
            Log(_recordResult);
        }
        UpdateStatus();
    }

    private void ShowRecordingResult(InterpolatIt.Core.Recording.RecordingStatus? done)
    {
        if (done is null) return;
        if (done.Error is not null) _recordResult = "Recording failed: " + done.Error;
        else if (done.Frames == 0) _recordResult = "Nothing was recorded.";
        else
        {
            double fps = done.Seconds > 0 ? done.Frames / done.Seconds : 0;
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            string folder = Path.GetDirectoryName(done.Path) ?? "";
            string where = folder.StartsWith(videos, StringComparison.OrdinalIgnoreCase) ? "Videos" + folder[videos.Length..] : folder;
            _recordResult = $"Saved {Path.GetFileName(done.Path)} in {where}  ·  {done.Seconds:F1} s, {fps:F0} frames per second, {done.Codec}" +
                            (done.Sound ? ", with sound" : done.SoundProblem is null ? ", without sound" : $", without sound: {done.SoundProblem}") +
                            (done.Dropped > 0 ? $", {done.Dropped} frames skipped" : "") +
                            (done.EndedBecause is null ? "" : $"  ·  ended because {done.EndedBecause}");
        }
        Log($"recording ended: {_recordResult} ({done.Frames} frames, {done.Dropped} dropped, {done.Path})");
    }

    // ------------------------------------------------------------------ window list

    private void RefreshWindows(nint select)
    {
        nint keep = select != 0 ? select : (WindowList.SelectedItem as WindowItem)?.Info.Handle ?? 0;
        var items = WindowEnumerator.GetCapturableWindows().Select(w => new WindowItem(w)).ToList();
        WindowList.ItemsSource = items;
        WindowList.SelectedItem = items.FirstOrDefault(i => i.Info.Handle == keep) ?? items.FirstOrDefault();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindows(0);

    private void WindowList_DropDownOpened(object? sender, EventArgs e) => RefreshWindows(0);

    // ------------------------------------------------------------------ options

    private int SelectedFactor => Factor4.IsChecked == true ? 4 : Factor3.IsChecked == true ? 3 : 2;

    private string SelectedModel => (QualityList.SelectedItem as ComboBoxItem)?.Tag as string ?? "rife-v4.25";

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Factor = SelectedFactor;
        _settings.Model = SelectedModel;
        SaveSettings();
        // A running session picks the change up by restarting on the same window (the model stays cached).
        if (_session is not null && !_busy) _ = StartAsync(_target);
    }

    private void EnabledSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_session is not null) _session.Enabled = EnabledSwitch.IsChecked == true;
        if (_loading) return;
        Log($"switch: interpolation {(EnabledSwitch.IsChecked == true ? "ON" : "OFF")}");
        UpdateStatus();
    }

    private void FpsSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_session is not null) _session.HudVisible = FpsSwitch.IsChecked == true;
        if (_loading) return;
        Log($"switch: fps readout {(FpsSwitch.IsChecked == true ? "shown" : "hidden")}");
        _settings.ShowFps = FpsSwitch.IsChecked == true;
        SaveSettings();
    }

    private void EnhanceSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_session is not null) _session.Enhance = EnhanceSwitch.IsChecked == true;
        if (_loading) return;
        Log($"switch: clean-up {(EnhanceSwitch.IsChecked == true ? "ON" : "OFF")}");
        _settings.Enhance = EnhanceSwitch.IsChecked == true;
        SaveSettings();
        UpdateStatus();
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void SaveSettings()
    {
        if (!_unattended) SettingsStore.Save(_settings);
    }

    // ------------------------------------------------------------------ start / stop

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_session is not null)
        {
            await StopAsync();
            return;
        }
        if (WindowList.SelectedItem is not WindowItem item)
        {
            HintText.Text = "Choose the window to interpolate first.";
            return;
        }
        await StartAsync(item.Info.Handle);
    }

    private async Task StartAsync(nint hwnd)
    {
        _busy = true;
        StartButton.IsEnabled = false;
        try
        {
            await StopCoreAsync();
            var options = new EngineOptions
            {
                TargetWindow = hwnd,
                Factor = SelectedFactor,
                ModelId = SelectedModel,
                BackendId = string.IsNullOrEmpty(_settings.Backend) ? null : _settings.Backend,
                ShowHud = FpsSwitch.IsChecked == true,
                Enhance = EnhanceSwitch.IsChecked == true,
                StartEnabled = EnabledSwitch.IsChecked == true,
                PacingLogPath = _startup.PacingLogPath is string pacing
                    ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pacing))!,
                        $"{Path.GetFileNameWithoutExtension(pacing)}-{_sessionNumber + 1}{Path.GetExtension(pacing)}")
                    : null,
            };
            _sessionNumber++;
            Log($"start #{_sessionNumber}: x{options.Factor}, {options.ModelId}");
            StateText.Text = "Starting…";
            InterpolationSession session;
            try
            {
                // Creating the device and measuring the refresh rate takes a moment: keep the window responsive.
                session = await Task.Run(() => InterpolationSession.Start(options));
            }
            catch (Exception ex)
            {
                // Whatever went wrong while starting (a window that may not be captured, a missing model file,
                // a graphics error): the user asked for a start and gets told why it did not happen.
                HintText.Text = "Could not start: " + ex.Message;
                Log($"could not start: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            session.Stopped += reason => Dispatcher.BeginInvoke(() => OnSessionStopped(session, reason));
            _session = session;
            _target = hwnd;
            HintText.Text = _defaultHint;
        }
        finally
        {
            _busy = false;
            StartButton.IsEnabled = true;
            UpdateStatus();
        }
    }

    private async Task StopAsync()
    {
        _busy = true;
        StartButton.IsEnabled = false;
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _busy = false;
            StartButton.IsEnabled = true;
            UpdateStatus();
        }
    }

    private async Task StopCoreAsync()
    {
        var session = _session;
        _session = null;
        if (session is null) return;
        bool wasRecording = session.RecordingState is { Finished: false };
        await Task.Run(session.Dispose);                    // also completes a running recording
        if (wasRecording) ShowRecordingResult(session.RecordingState);
    }

    /// <summary>The session ended by itself: the target window was closed, or something failed.</summary>
    private void OnSessionStopped(InterpolationSession session, string reason)
    {
        if (!ReferenceEquals(session, _session)) return;
        _session = null;
        bool failed = session.Stats.State == EngineState.Failed;
        _ = Task.Run(session.Dispose);
        HintText.Text = (failed ? "Stopped because of an error: " : "Stopped: ") + reason;
        Log(HintText.Text);
        UpdateStatus();
    }

    // ------------------------------------------------------------------ status

    private void UpdateStatus()
    {
        var session = _session;
        StartButton.Content = session is null ? "Start Interpolating" : "Stop";
        WindowList.IsEnabled = session is null && !_busy;

        var recording = session?.RecordingState;
        bool isRecording = recording is { Finished: false };
        if (recording is { Finished: true } ended && _recordResult is null) ShowRecordingResult(ended);     // it ended by itself
        RecordButton.IsEnabled = session is not null;
        RecordButton.Content = isRecording ? "Stop recording" : "Record";
        RecordText.Text = isRecording
            ? recording!.Active
                ? $"Recording  {TimeSpan.FromSeconds(recording.Seconds):m\\:ss}  ·  {recording.Frames} frames" + (recording.Dropped > 0 ? $"  ·  {recording.Dropped} skipped" : "") +
                  (recording.Sound ? "  ·  with sound" : recording.SoundProblem is null ? "" : $"  ·  without sound: {recording.SoundProblem}")
                : "Starting the encoder…"
            : _recordResult ?? _recordIdleText;

        if (session is null)
        {
            if (!_busy) StateText.Text = "Not running";
            SourceFpsText.Text = TargetFpsText.Text = ActualFpsText.Text = "–";
            DetailText.Text = "";
            EnhanceText.Visibility = Visibility.Collapsed;
            return;
        }

        EngineStats s = session.Stats;
        // The driver never says whether it really enhances; the engine finds out and this line tells.
        if (EnhanceSwitch.IsChecked != true) EnhanceText.Visibility = Visibility.Collapsed;
        else
        {
            EnhanceText.Visibility = Visibility.Visible;
            EnhanceText.Text = s.Enhancing
                ? $"Active  ·  {s.EnhanceMs:F1} ms per source frame" +
                  (s.EnhanceWidth != s.Width || s.EnhanceHeight != s.Height ? $", through {s.EnhanceWidth}×{s.EnhanceHeight}" : "")
                : s.EnhanceProblem is string problem ? "Not active: " + problem
                : "Starts as soon as frames are being added";
        }
        StateText.Text = s.State switch
        {
            EngineState.Starting => "Starting…" + (s.Message is null ? "" : "  " + s.Message),
            EngineState.Waiting => "Waiting for movement in the window",
            EngineState.Interpolating => "Interpolating" + (s.Pacing.Length > 0 ? "  ·  " + s.Pacing : ""),
            EngineState.Bypass => session.Enabled ? "Standing by: " + (s.Message ?? "nothing to add") : "Switched off  ·  the window shows as it is",
            EngineState.Failed => "Failed: " + s.Message,
            _ => "Stopped",
        };
        bool live = s.State is EngineState.Interpolating;
        SourceFpsText.Text = s.SourceFps > 0 ? s.SourceFps.ToString("F1") : "–";
        TargetFpsText.Text = live && s.TargetFps > 0 ? s.TargetFps.ToString("F1") : "–";
        ActualFpsText.Text = live ? s.ActualFps.ToString("F1") : "–";

        var details = new List<string>(4);
        if (live) details.Add($"{s.DelayMs:F0} ms behind the real window");
        if (s.InferenceMs > 0) details.Add($"AI {s.InferenceMs:F1} ms per frame at {s.InferenceWidth}×{s.InferenceHeight}");
        if (s.Backend.Length > 0) details.Add(s.Backend);
        DetailText.Text = string.Join("  ·  ", details);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _hotkeys?.Dispose();
        _session?.Dispose();
        _session = null;
        SaveSettings();
        Log("closed");
        base.OnClosed(e);
    }
}
