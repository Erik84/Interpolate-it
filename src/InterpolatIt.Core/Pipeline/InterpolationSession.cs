using InterpolatIt.Core.Capture;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Native;
using InterpolatIt.Core.Presentation;
using InterpolatIt.Core.Recording;
using InterpolatIt.Timing;

namespace InterpolatIt.Core.Pipeline;

/// <summary>
/// One running "interpolate this window" session: owns the GPU device, the capture, the overlay and the two
/// worker threads (pipeline: capture -> analysis -> inference -> synthesis; presenter: paced presentation).
/// </summary>
public sealed partial class InterpolationSession : IDisposable
{
    private readonly SessionContext _ctx;
    private readonly Thread _pipelineThread, _presenterThread;
    private readonly PacingLog? _log;
    private bool _disposed;

    public EngineOptions Options { get; }
    public EngineStats Stats => _ctx.Stats;

    /// <summary>Raised (on a worker thread) when the session ends by itself: target closed, or a failure.</summary>
    public event Action<string>? Stopped;

    /// <summary>The on/off switch. Off cloaks the overlay at the next refresh and stops generating; capture and
    /// the compiled model stay loaded, so switching back on is immediate.</summary>
    public bool Enabled
    {
        get => _ctx.Enabled;
        set
        {
            _ctx.Enabled = value;
            if (!value) _ctx.Stats.State = EngineState.Bypass;
        }
    }

    /// <summary>Clean up compressed video before interpolating (the graphics driver's enhancement, once per
    /// real frame). Whether it is really being applied, and why not, is in <see cref="EngineStats.Enhancing"/>
    /// and <see cref="EngineStats.EnhanceProblem"/> a moment later.</summary>
    public bool Enhance
    {
        get => _ctx.Enhance;
        set => _ctx.Enhance = value;
    }

    /// <summary>Shows or hides the fps readout in the overlay.</summary>
    public bool HudVisible { get; set; }

    public bool IsRunning => !_ctx.Stop.IsCancellationRequested;
    public string? StopReason => _ctx.StopReason;

    // ------------------------------------------------------------------ recording

    private Mp4Recorder? _recorder;

    /// <summary>The current or most recent recording of this session; null when there has been none.</summary>
    public RecordingStatus? RecordingState => _recorder?.Status;

    /// <summary>
    /// Starts writing what is shown in the target window to an MP4 file: the interpolated picture while the
    /// overlay is up (at up to the display's refresh rate), the window's own frames otherwise. Returns at once.
    /// </summary>
    public RecordingStatus StartRecording(string path, RecordingOptions? options = null)
    {
        if (_recorder is { } running && !running.Status.Finished)
            throw new InvalidOperationException("A recording is already running.");
        int width = _ctx.ContentWidth, height = _ctx.ContentHeight;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("There is no picture to record yet; wait until the window is being captured.");
        double refreshRate;
        lock (_ctx.Timing) refreshRate = 1.0 / _ctx.Clock.Period;
        if (options is null)
        {
            // With the sound of the application whose window this is. While the overlay is up, the picture that is
            // recorded runs behind the application's own by the playback delay; the sound does not, so it is put
            // that much later in the file and the two belong together again there.
            User32.GetWindowThreadProcessId(Options.TargetWindow, out uint processId);
            bool delayed = _ctx.Stats.State == EngineState.Interpolating && !_ctx.Overlay.IsCloaked;
            options = new RecordingOptions
            {
                AudioProcessId = Options.RecordSound ? processId : 0,
                AudioDelay = delayed ? _ctx.Stats.DelayMs / 1000 : 0,
            };
        }
        _recorder?.Dispose();
        _recorder = Mp4Recorder.Start(_ctx.Gpu, path, width, height, refreshRate, options);
        _ctx.Recorder = _recorder;
        return _recorder.Status;
    }

    /// <summary>Ends the recording and returns once the file is complete (up to a few seconds).</summary>
    public RecordingStatus? StopRecording()
    {
        var recorder = _recorder;
        if (recorder is null) return null;
        _ctx.Recorder = null;           // no more frames from the worker threads
        recorder.StopAndWait();
        return recorder.Status;
    }

    private InterpolationSession(EngineOptions options)
    {
        Options = options;
        nint hwnd = options.TargetWindow;
        var tracker = new WindowTracker(hwnd);
        TargetState target = tracker.Poll();
        if (!target.Exists) throw new InvalidOperationException("The target window does not exist.");
        if (target.Minimized) throw new InvalidOperationException("The target window is minimised; restore it first.");
        if (target.Bounds.IsEmpty) throw new InvalidOperationException("The target window has no visible area.");

        Kernel32.OptOutOfPowerThrottling();
        double refreshPeriod = DisplayClock.MeasureRefreshPeriod();
        var gpu = GpuDevice.Create();
        string gpuPriority = options.RaiseGpuPriority ? gpu.RaiseSchedulingPriority() : "not raised";
        OverlayWindow overlay;
        try
        {
            overlay = new OverlayWindow(hwnd, target.Bounds);
        }
        catch
        {
            gpu.Dispose();
            throw;
        }
        var clock = new VBlankClock(refreshPeriod);
        if (options.PacingLogPath is not null) _log = new PacingLog(options.PacingLogPath);
        _ctx = new SessionContext
        {
            Options = options,
            Gpu = gpu,
            Overlay = overlay,
            Tracker = tracker,
            Clock = clock,
            // Present-to-glass lag: a present made in interval r is reported as shown at refresh r + 2 while DWM
            // composes the overlay (measured; the presenter keeps verifying it with frame statistics).
            Scheduler = new FrameScheduler(clock, options.Factor, lag: 2)
            {
                Phase = Math.Clamp(options.PresentPhase, 0.05, 0.95),
            },
            Stats = new EngineStats { RefreshHz = 1.0 / refreshPeriod, GpuPriority = gpuPriority },
            Log = _log,
            Phase = Math.Clamp(options.PresentPhase, 0.05, 0.95),
            SyncInterval = (uint)Math.Clamp(options.SyncInterval, 0, 1),
            CalibrateLag = options.CalibrateLag,
        };
        _ctx.Enabled = options.StartEnabled;
        _ctx.Enhance = options.Enhance;
        HudVisible = options.ShowHud;
        _hud = new Hud(gpu);
        _ctx.DrawHud = DrawHud;
        _ctx.StopRequested += () => Stopped?.Invoke(_ctx.StopReason ?? "stopped");

        _presenterThread = new Thread(new Presenter(_ctx).Run) { IsBackground = true, Name = "InterpolatIt presenter" };
        _pipelineThread = new Thread(PipelineMain) { IsBackground = true, Name = "InterpolatIt pipeline", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Starts interpolating the target window. Throws when the window cannot be captured or covered.</summary>
    public static InterpolationSession Start(EngineOptions options)
    {
        var session = new InterpolationSession(options);
        session._presenterThread.Start();
        session._pipelineThread.Start();
        return session;
    }

    private readonly Hud _hud;
    private double _hudNextUpdate;

    /// <summary>
    /// Draws the fps readout into the back buffer. Runs on the presenter thread under the GPU lock, just before
    /// Present; the text is refreshed four times per second.
    /// </summary>
    private void DrawHud(WindowSwapChain chain)
    {
        if (!HudVisible) return;
        double now = Qpc.Now;
        if (now >= _hudNextUpdate)
        {
            _hudNextUpdate = now + 0.25;
            var s = _ctx.Stats;
            _hud.SetText(Hud.FpsLines(s.SourceFps, s.TargetFps, s.ActualFps), scale: chain.Height >= 1600 ? 3 : 2);
        }
        // Inside the client area of a normal window: below a title bar, clear of the rounded corner.
        _hud.Draw(chain.BackBuffer, chain.Width, chain.Height, 16, Math.Min(48, Math.Max(0, chain.Height - 82)));
    }

    /// <summary>Refreshes between a frame's own time on the glass and its copy in the overlay (delay line).</summary>
    private const int PassthroughDelay = 1;

    /// <summary>
    /// The refresh at which a captured frame is on the glass in the real window. The capture timestamp is that
    /// vblank (measured: the frame reaches us about 7 ms before its own timestamp on a 120 Hz display); half a
    /// period is added because our tick times trail the true vblank by the thread wake-up latency.
    /// </summary>
    internal static long GlassRefresh(VBlankClock clock, double captureTimestamp)
        => clock.CountAt(captureTimestamp + 0.5 * clock.Period);

    private void PipelineMain()
    {
        try
        {
            if (Options.Passthrough) RunPassthrough();
            else RunInterpolation();
        }
        catch (Exception e)
        {
            _ctx.RequestStop($"pipeline failed: {e.GetType().Name}: {e.Message}", failed: true);
        }
    }

    /// <summary>
    /// Delay line: every captured frame is shown unchanged in the overlay, one refresh after it is on the glass
    /// in the real window. That is the minimum: the frame arrives about a millisecond after DWM composed it and
    /// has to be presented in that same refresh interval. Exercises capture, the frame store, the overlay and
    /// the presenter without any inference.
    /// </summary>
    private void RunPassthrough()
    {
        var ctx = _ctx;
        using var capture = new WgcCapture(ctx.Gpu, Options.TargetWindow);
        using var store = new FrameStore(ctx.Gpu, realCapacity: 8, generatedCapacity: 1);
        WaitHandle[] handles = [capture.FrameSignal, ctx.Stop.Token.WaitHandle];
        long id = 0;
        double lastTime = double.NaN;
        ctx.Stats.Pacing = "passthrough";
        ctx.Stats.State = EngineState.Waiting;

        while (!ctx.Stop.IsCancellationRequested)
        {
            WaitHandle.WaitAny(handles, 100);
            if (capture.IsClosed) { ctx.RequestStop("the target window was closed"); break; }
            while (capture.TryAcquire(out CapturedFrame? frame))
            {
                using (frame)
                {
                    ctx.Stats.Arrived.Mark();
                    if (!ctx.Enabled) continue;
                    int w = frame!.ContentWidth, h = frame.ContentHeight;
                    if (w != store.Width || h != store.Height)
                    {
                        ctx.Queue.Clear();
                        store.Configure(w, h);
                        ctx.ContentWidth = w;
                        ctx.ContentHeight = h;
                        ctx.Stats.Width = w;
                        ctx.Stats.Height = h;
                    }
                    GpuFrame stored = store.StoreReal(frame, id);
                    if (ctx.Recorder is { } recorder && ctx.Overlay.IsCloaked) recorder.AddFrame(stored.Texture, w, h, frame.Time);
                    long refresh;
                    double period;
                    lock (ctx.Timing)
                    {
                        period = ctx.Clock.Period;
                        refresh = GlassRefresh(ctx.Clock, frame.Time) + PassthroughDelay;
                    }
                    ctx.Queue.Enqueue(new QueuedFrame(refresh, stored, PlannedKind.Real, id, id, 0f));
                    ctx.Stats.Accepted.Mark();
                    if (!double.IsNaN(lastTime) && frame.Time > lastTime)
                    {
                        ctx.SourcePeriod = 0.9 * ctx.SourcePeriod + 0.1 * (frame.Time - lastTime);
                        ctx.Stats.SourceFps = 1.0 / ctx.SourcePeriod;
                    }
                    ctx.Stats.TargetFps = ctx.Stats.SourceFps;
                    ctx.Stats.DelayMs = PassthroughDelay * period * 1000;
                    lastTime = frame.Time;
                    id++;
                }
            }
            _log?.Flush();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_ctx.Stop.IsCancellationRequested)
        {
            _ctx.Stats.State = EngineState.Stopped;
            _ctx.Stop.Cancel();
        }
        _recorder?.Stop();              // no new frames; the file is finished below, once the threads are gone
        if (_presenterThread.IsAlive) _presenterThread.Join(3000);
        if (_pipelineThread.IsAlive) _pipelineThread.Join(5000);
        _ctx.Recorder = null;
        _recorder?.Dispose();
        _hud.Dispose();
        _ctx.Queue.Dispose();
        _ctx.Overlay.Dispose();
        _log?.Dispose();
        _ctx.Gpu.Dispose();
    }
}
