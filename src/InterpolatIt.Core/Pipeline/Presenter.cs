using InterpolatIt.Core.Capture;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Native;
using InterpolatIt.Core.Presentation;
using InterpolatIt.Timing;
using Vortice.DXGI;

namespace InterpolatIt.Core.Pipeline;

/// <summary>
/// The presentation loop: one decision per display refresh.
/// <para>
/// It wakes on the compositor clock (DWM's tick at the display's vblank), keeps the overlay glued to the target
/// window, and at a fixed phase inside the refresh interval presents the frame that is due.
/// </para>
/// <para>
/// Refresh numbering used throughout the engine ("glass" convention): a frame belongs to the refresh at which
/// it is on the display. Capture timestamps are already in that convention (Windows.Graphics.Capture stamps a
/// frame with the vblank at which it reaches the glass, and hands it over about one refresh earlier), and so
/// are DXGI frame statistics for our own presents.
/// </para>
/// <para>
/// Measured on Windows 11 26300 (frame statistics and the screen probe agree): a frame presented in the middle
/// of interval r is shown at refresh r + 2. The sync interval decides how reliably:
/// with 0 the frame is available to the compositor at once, and the compositor's own latch moment moves around
/// the tick by a millisecond or more (further under GPU load), so 0.5-7 % of the frames landed one refresh
/// early at phase 0.5, 6 % early at phase 0.15 and 9 % late at phase 0.9. With 1 the present takes effect at
/// the next vblank whatever the compositor does: 729 of 730 frames on plan, at the same lag. Hence sync
/// interval 1, presented mid-interval (very late presents, phase 0.85, missed the vblank 2 % of the time).
/// </para>
/// </summary>
internal sealed class Presenter
{
    /// <summary>
    /// Earliest point in a refresh interval at which a waiting frame is presented. Measured with sync interval 1:
    /// presents at phase 0.1 landed on the planned refresh 1107 times out of 1107 (0.25: 1122 of 1124, 0.5: 1106
    /// of 1111, under the same load) - the earlier the copy is submitted, the less a busy GPU can delay it.
    /// </summary>
    private const double EarliestPhase = 0.1;

    private readonly SessionContext _ctx;
    private readonly (uint PresentId, long Planned)[] _presentRing = new (uint, long)[64];
    private int _presentRingNext;
    private uint _lastStatPresentCount;
    private readonly sbyte[] _lagWindow = new sbyte[40];
    private int _lagWindowNext, _lagWindowCount;

    public Presenter(SessionContext ctx) => _ctx = ctx;

    public void Run()
    {
        var ctx = _ctx;
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        uint taskIndex = 0;
        nint mmcss = Avrt.AvSetMmThreadCharacteristicsW("Games", ref taskIndex);
        using var timer = new HighResolutionTimer();
        WindowSwapChain? chain = null;
        try
        {
            double period = ctx.Clock.Period;
            long refresh = 0;
            double lastTick = double.NaN, lastPresent = double.NegativeInfinity;
            int presentsWhileCloaked = 0, timeouts = 0;
            RECT bounds = ctx.Overlay.Bounds;
            double nextLogFlush = Qpc.Now + 0.5, nextOverlayCheck = 0;

            while (!ctx.Stop.IsCancellationRequested)
            {
                if (!DisplayClock.WaitForTick(100))
                {
                    // Display off, session locked or remote session: nothing can be shown. Do not spin.
                    if (++timeouts > 5) { ctx.Overlay.SetCloaked(true); Thread.Sleep(50); }
                    continue;
                }
                timeouts = 0;
                double tick = Qpc.Now;
                long steps = double.IsNaN(lastTick) ? 1 : Math.Max(1, (long)Math.Round((tick - lastTick) / period));
                refresh += steps;
                lastTick = tick;
                int lag;
                lock (ctx.Timing)
                {
                    ctx.Clock.Update(refresh, tick);
                    period = ctx.Clock.Period;
                    lag = ctx.Scheduler.Lag;
                }
                ctx.Stats.RefreshHz = 1.0 / period;
                ctx.Stats.Lag = lag;

                // ---- the target window ------------------------------------------------------------------
                TargetState target = ctx.Tracker.Poll();
                if (!target.Exists)
                {
                    ctx.RequestStop("the target window was closed");
                    break;
                }
                bool coverable = target.Coverable && ctx.Enabled;
                if (coverable && (target.Bounds.Width != ctx.ContentWidth || target.Bounds.Height != ctx.ContentHeight))
                    coverable = false;      // the capture has not caught up with a resize yet
                if (!coverable)
                {
                    if (!ctx.Overlay.IsCloaked) ctx.Overlay.SetCloaked(true);
                    presentsWhileCloaked = 0;
                    ctx.Queue.Clear();
                    Log(tick, refresh, "cloak", lag, 0, null, 0, 0, chain, period);
                    continue;
                }
                if (!Same(target.Bounds, bounds) || chain is null)
                {
                    bool resized = chain is null || target.Bounds.Width != bounds.Width || target.Bounds.Height != bounds.Height;
                    bounds = target.Bounds;
                    ctx.Overlay.SetBounds(bounds);
                    ctx.Overlay.SetRoundedCorners(!target.FillsMonitor);
                    if (resized)
                    {
                        ctx.Overlay.SetCloaked(true);
                        presentsWhileCloaked = 0;
                        if (chain is null) chain = new WindowSwapChain(ctx.Gpu, ctx.Overlay.Handle, bounds.Width, bounds.Height);
                        else chain.Resize(bounds.Width, bounds.Height);
                        ctx.Queue.Clear();
                    }
                }
                if (chain is null) continue;
                // One refresh after the first present the new picture is the window's content: safe to show.
                if (ctx.Overlay.IsCloaked && presentsWhileCloaked >= 1)
                {
                    ctx.Overlay.SetCloaked(false);
                    ctx.Stats.State = ctx.Options.Passthrough ? EngineState.Bypass : EngineState.Interpolating;
                }

                // ---- present the frame that is due ---------------------------------------------------------
                // As early as possible, as late as necessary. A present made anywhere in this interval is shown
                // at refresh + lag, so a frame that is already waiting goes out at once: its copy then has
                // nearly the whole interval to get through a busy GPU, and it is on its way before this
                // interval's capture arrives and starts an inference. A frame that is still being generated is
                // waited for until the phase the scheduler's deadlines are based on; only then does an overdue
                // frame get the slot.
                double latest = tick + ctx.Phase * period;
                timer.WaitUntil(Math.Min(latest, tick + EarliestPhase * period));
                long due = refresh + lag;
                bool had;
                QueuedFrame frame;
                int stale;
                while (!(had = ctx.Queue.TryTakeDue(due, onTimeOnly: true, out frame, out stale)))
                {
                    if (Qpc.Now >= latest)
                    {
                        had = ctx.Queue.TryTakeDue(due, onTimeOnly: false, out frame, out stale);
                        break;
                    }
                    timer.WaitUntil(latest, ctx.Queue.ArrivedHandle);
                }
                if (stale > 0) ctx.Stats.CountStale(stale);
                if (had && frame.Frame.Width == chain.Width && frame.Frame.Height == chain.Height)
                {
                    uint presentId;
                    lock (ctx.Gpu.Sync)
                    {
                        ctx.Gpu.Context.CopyResource(chain.BackBuffer, frame.Frame.Texture);
                        ctx.DrawHud?.Invoke(chain);
                        chain.Present(ctx.SyncInterval);
                        presentId = chain.LastPresentCount;
                        // The recording takes the frame itself (without the fps readout), stamped with the
                        // refresh it is shown on. While the overlay is still cloaked the window's own frames
                        // are what is on screen, and the pipeline records those.
                        if (!ctx.Overlay.IsCloaked)
                            ctx.Recorder?.AddFrame(frame.Frame.Texture, frame.Frame.Width, frame.Frame.Height, tick + lag * period);
                    }
                    double now = Qpc.Now;
                    ctx.Stats.Presented.Mark(now);
                    lastPresent = now;
                    int late = (int)(due - frame.Refresh);
                    if (late > 0) ctx.Stats.CountLate();
                    if (ctx.Overlay.IsCloaked) presentsWhileCloaked++;
                    _presentRing[_presentRingNext] = (presentId, due);
                    _presentRingNext = (_presentRingNext + 1) % _presentRing.Length;
                    Log(tick, refresh, "present", lag, late, frame, ctx.Queue.Count, presentId, chain, period);
                }
                else
                {
                    // Nothing due. If the source has gone quiet, step aside: the real window then shows with no
                    // delay at all, which is what the user wants while a video is paused or a page sits still.
                    // Not too eagerly, though: stepping aside and coming back each make the picture jump by the
                    // playback delay, and a stream that hiccups for a tenth of a second (measured on a game
                    // streamed over the network: several times a minute) should just stand still for that long.
                    // Until then the last frame stays up - the newest real one, see the pipeline's flush.
                    double idle = Math.Max(6 * ctx.SourcePeriod, 0.5);
                    if (!ctx.Overlay.IsCloaked && tick - lastPresent > idle)
                    {
                        ctx.Overlay.SetCloaked(true);
                        presentsWhileCloaked = 0;
                        ctx.Stats.State = EngineState.Waiting;
                    }
                    Log(tick, refresh, had ? "size" : "none", lag, 0, null, ctx.Queue.Count, 0, chain, period);
                }

                // Ten times a second: is the overlay really where it should be? (Measured: in two of twelve test
                // runs it had ended up underneath its target - an owned window is not kept above an owner from
                // another process as reliably as the documentation suggests - and nothing but this check noticed.)
                if (tick >= nextOverlayCheck)
                {
                    nextOverlayCheck = tick + 0.1;
                    if (ctx.Overlay.Verify() is string problem)
                    {
                        ctx.Stats.CountOverlayRepair(problem);
                        if (ctx.Options.TraceEvents) Console.Error.WriteLine($"overlay: {problem}; corrected");
                    }
                }

                if (tick >= nextLogFlush)
                {
                    nextLogFlush = tick + 0.5;
                    lock (ctx.Timing) ctx.Scheduler.Control(tick);
                }
            }
        }
        catch (Exception e)
        {
            ctx.RequestStop($"presenter failed: {e.GetType().Name}: {e.Message}", failed: true);
        }
        finally
        {
            ctx.Overlay.SetCloaked(true);
            chain?.Dispose();
            if (mmcss != 0) Avrt.AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    private static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>
    /// Writes the pacing row and uses DXGI frame statistics to check on which refresh our presents really became
    /// visible, i.e. whether the assumed lag is right.
    /// </summary>
    private void Log(double tick, long refresh, string action, int lag, int late, QueuedFrame? frame, int queue,
        uint presentId, WindowSwapChain? chain, double period)
    {
        var ctx = _ctx;
        FrameStatistics s = default;
        bool hasStats = chain is not null && chain.TryGetFrameStatistics(out s);
        double syncTime = hasStats ? Qpc.FromTicks(s.SyncQPCTime) : 0;

        if (hasStats && s.PresentCount != _lastStatPresentCount && s.SyncQPCTime != 0)
        {
            _lastStatPresentCount = s.PresentCount;
            foreach (var (id, planned) in _presentRing)
            {
                if (id != s.PresentCount || id == 0) continue;
                // When did that present reach the screen, in our own refresh numbering?
                double shownAt = syncTime + ((long)s.PresentRefreshCount - s.SyncRefreshCount) * period;
                long shown;
                lock (ctx.Timing) shown = ctx.Clock.CountAt(shownAt + 0.25 * period);
                int delta = (int)(shown - planned);
                ctx.LastLagError = delta;
                Interlocked.Increment(ref ctx.LagHistogram[Math.Clamp(delta, -2, 2) + 2]);

                // The lag is a property of how the overlay is being composed and only changes when that does
                // (composed <-> direct flip). Single early or late frames are jitter in the compositor's own
                // timing; reacting to them would shift the whole schedule back and forth. So: change the lag
                // only when nearly all of the last presents agree on the same error.
                _lagWindow[_lagWindowNext] = (sbyte)Math.Clamp(delta, -3, 3);
                _lagWindowNext = (_lagWindowNext + 1) % _lagWindow.Length;
                if (_lagWindowCount < _lagWindow.Length) _lagWindowCount++;
                if (delta != 0 && _lagWindowCount == _lagWindow.Length && ctx.CalibrateLag)
                {
                    int agree = 0;
                    foreach (sbyte d in _lagWindow) if (d == delta) agree++;
                    if (agree >= _lagWindow.Length * 9 / 10)
                    {
                        lock (ctx.Timing) ctx.Scheduler.OnLagChanged(Math.Clamp(lag + delta, 1, 5));
                        _lagWindowCount = 0;
                    }
                }
                break;
            }
        }

        ctx.Log?.Row(tick, refresh, action, frame?.Refresh ?? 0, frame is null ? "" : frame.Value.Kind == PlannedKind.Real ? "real" : "interp",
            frame?.FrameA ?? -1, frame?.FrameB ?? -1, frame?.T ?? 0, late, queue, ctx.Overlay.IsCloaked, lag, presentId,
            hasStats ? s.PresentCount : 0, hasStats ? s.PresentRefreshCount : 0, hasStats ? s.SyncRefreshCount : 0, syncTime);
    }
}
