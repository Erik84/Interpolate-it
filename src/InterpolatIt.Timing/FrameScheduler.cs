namespace InterpolatIt.Timing;

/// <summary>
/// Turns accepted source frames into a plan: which output frame (a real frame, or an interpolation at timestep t)
/// becomes visible on which display refresh.
/// <para>
/// Planning happens when source frame B arrives, for the stretch between the previous frame A and B. Stepped
/// mode counts slots (slot = v0 + ((tick - tick0) * N + k) * m), so phases never flip on timing jitter;
/// continuous mode gives every m-th refresh the content time it stands for (time of refresh minus the playback
/// delay D). A frame between A and B can only be made once B is here, so D is at least the interpolation
/// window plus capture latency, generation time and the present lead.
/// </para>
/// </summary>
public sealed class FrameScheduler
{
    private readonly VBlankClock _clock;
    private readonly SlackController _slack = new();

    private PacingMode _mode;
    private bool _anchored;
    private long _prevId;
    private SourceStamp _prev;
    private long _lastPlanned = long.MinValue;

    // stepped
    private long _tick0, _slot0, _nextIndex;
    // continuous
    private long _nextSlot;
    private double _delayTarget;

    public FrameScheduler(VBlankClock clock, int factor, int lag = 2)
    {
        _clock = clock;
        Factor = factor;
        Lag = lag;
    }

    /// <summary>Requested multiplier N.</summary>
    public int Factor { get; set; }

    /// <summary>L: a frame presented during refresh interval u is the one on screen for refresh u + L. What a
    /// "refresh" is anchored to (the compositor's tick or the glass) is the caller's convention, as long as the
    /// source timestamps use the same one; the lag is measured, not assumed (see OnLagChanged).</summary>
    public int Lag { get; private set; }

    /// <summary>phi: position inside the refresh interval at which Present is called (0..1).</summary>
    public double Phase { get; set; } = 0.5;

    /// <summary>Safety margin kept between "frame ready" and its deadline.</summary>
    public double Guard { get; set; } = 0.001;

    /// <summary>Continuous mode shows the real frame when a slot is within this fraction of it. RIFE itself
    /// returns the nearest frame for timesteps below about 0.07, so nothing is lost and an inference is saved.</summary>
    public double Epsilon { get; set; } = 0.06;

    public PacingMode Mode => _mode;

    /// <summary>D: seconds between a source frame's timestamp and the refresh that shows it.</summary>
    public double Delay { get; private set; }

    public long ReanchorCount { get; private set; }
    public long SlipCount { get; private set; }

    /// <summary>Time between calling Present for a frame and the refresh that shows it.</summary>
    public double Lead => (Lag - Phase) * _clock.Period;

    /// <summary>A frame planned for <paramref name="refresh"/> must be complete on the GPU by this time.</summary>
    public double Deadline(long refresh) => _clock.TimeOf(refresh) - Lead;

    public void Reset()
    {
        _anchored = false;
        _lastPlanned = long.MinValue;
        _lastShownReal = -1;
        _slack.Clear();
    }

    /// <summary>
    /// Plans the output frames that source frame <paramref name="frameId"/> makes possible and appends them to
    /// <paramref name="plan"/> in refresh order.
    /// </summary>
    /// <param name="stamp">Result of <see cref="CadenceEstimator.Accept"/> for this frame.</param>
    /// <param name="cutBefore">A scene cut separates this frame from the previous one: nothing is interpolated across it.</param>
    /// <param name="now">Current time (the frame is in our hands and its analysis is done).</param>
    /// <param name="generation">Expected time from now until the first generated frame of a pair is ready (p95).</param>
    /// <param name="freePeriod">Median source interval, used as the period while the cadence is free.</param>
    public void OnSourceFrame(long frameId, in SourceStamp stamp, bool cutBefore, double now, double generation,
        double freePeriod, List<PlannedFrame> plan)
    {
        bool locked = stamp.Mode == CadenceMode.Locked;
        double period = locked ? stamp.Period : freePeriod;
        var mode = SelectMode(period, locked);

        if (!_anchored || stamp.Discontinuity || mode != _mode)
        {
            _mode = mode;
            Reanchor(frameId, stamp, period, now, generation, plan);
            _prev = stamp;
            _prevId = frameId;
            return;
        }

        var a = _prev;
        long aId = _prevId;
        _prev = stamp;
        _prevId = frameId;

        if (_mode.Kind == PacingKind.Stepped)
            PlanStepped(aId, a, frameId, stamp, cutBefore, plan);
        else
            PlanContinuous(aId, a, frameId, stamp, cutBefore, period, locked, plan);
    }

    private const double KeepUntilSlowerBy = 0.65, KeepUntilFasterBy = 0.45;

    private PacingMode SelectMode(double period, bool locked)
    {
        // Keep the current slot spacing unless the exact value has clearly moved on: the measured periods wobble,
        // and 24 fps "x2" on 120 Hz sits exactly between two spacings (2.5 refreshes per output frame).
        // The band is lopsided on purpose. A slower output is only taken when the exact value is well past the
        // midpoint; a faster one already at the midpoint, because a tie goes to the faster output wherever the
        // source came from (a game stream dipping from 60 to 30 fps and recovering to 40 was left at 60 fps
        // output, 1.5 frames per source frame, where 120 fps was just as near).
        if (_anchored && period > 0)
        {
            double off = PacingPolicy.ExactRefreshesPerFrame(period, _clock.Period, Factor) - _mode.RefreshesPerFrame;
            if (off < KeepUntilSlowerBy && off > -KeepUntilFasterBy)
                return PacingPolicy.Select(period, _clock.Period, Factor, locked, _mode.RefreshesPerFrame);
        }
        return PacingPolicy.Select(period, _clock.Period, Factor, locked);
    }

    private void Reanchor(long frameId, in SourceStamp b, double period, double now, double generation, List<PlannedFrame> plan)
    {
        double p = _clock.Period;
        int n = _mode.Factor, m = _mode.RefreshesPerFrame;
        bool hasPeriod = period > 0;
        double window = _mode.Kind == PacingKind.Stepped ? period * (1.0 - 1.0 / n) : (hasPeriod ? period : m * p);
        double capture = now - b.Time;      // not clamped: a capture API may deliver a frame ahead of its timestamp
        double minDelay = Math.Max(p, window + capture + Math.Max(0, generation) + Lead + Guard + 0.5 * p);

        if (_mode.Kind == PacingKind.Stepped)
        {
            _slot0 = Math.Max(_clock.FirstAtOrAfter(b.Time + minDelay), _lastPlanned == long.MinValue ? long.MinValue : _lastPlanned + 1);
            _tick0 = b.Tick;
            _nextIndex = 1;
            Delay = _clock.TimeOf(_slot0) - b.Time;
            Emit(plan, _slot0, PlannedKind.Real, frameId, frameId, 0f, b.Time);
        }
        else
        {
            // Put a slot exactly on this frame: the cycle of timesteps then contains the real frames themselves
            // (25 fps at 60 fps output repeats twelve phases; anchored anywhere else, none of them is a real
            // frame and the two nearest ones collapse onto the same picture).
            _nextSlot = _clock.FirstAtOrAfter(b.Time + minDelay);
            if (_lastPlanned != long.MinValue && _nextSlot <= _lastPlanned) _nextSlot = _lastPlanned + 1;
            Delay = _delayTarget = _clock.TimeOf(_nextSlot) - b.Time;
        }
        _anchored = true;
        _slack.Clear();
        ReanchorCount++;
    }

    private void PlanStepped(long aId, in SourceStamp a, long bId, in SourceStamp b, bool cut, List<PlannedFrame> plan)
    {
        int n = _mode.Factor, m = _mode.RefreshesPerFrame;
        long indexB = (b.Tick - _tick0) * n;            // slot index on which B itself is shown
        for (long j = _nextIndex; j <= indexB; j++)
        {
            long k = j - (indexB - n);                  // position inside the last source interval before B
            long refresh = _slot0 + j * m;
            if (k == n)
                Emit(plan, refresh, PlannedKind.Real, bId, bId, 0f, b.Time);
            else if (k <= 0 || cut)
                continue;                               // dropped-frame gap or scene cut: A stays on screen
            else
                Emit(plan, refresh, PlannedKind.Interpolated, aId, bId, (float)k / n, b.Time - (n - k) * b.Period / n);
        }
        _nextIndex = indexB + 1;
        Delay = _clock.TimeOf(_slot0 + indexB * m) - b.Time;
    }

    private void PlanContinuous(long aId, in SourceStamp a, long bId, in SourceStamp b, bool cut, double period,
        bool locked, List<PlannedFrame> plan)
    {
        int m = _mode.RefreshesPerFrame;
        double gap = b.Time - a.Time;
        double maxWindow = period > 0 ? (locked ? 1.25 : 1.5) * period : gap;
        double window = cut ? 0 : Math.Min(gap, maxWindow);     // only the last `window` before B is interpolated

        long v = _nextSlot;
        for (; ; v += m)
        {
            double sigma = _clock.TimeOf(v) - Delay;            // content time this slot stands for
            if (sigma > b.Time + 1e-7) break;
            double t = window > 0 ? (sigma - (b.Time - window)) / window : 0;
            if (t <= Epsilon)
                Emit(plan, v, PlannedKind.Real, aId, aId, 0f, a.Time);
            else if (t >= 1 - Epsilon)
                Emit(plan, v, PlannedKind.Real, bId, bId, 0f, b.Time);
            else
                Emit(plan, v, PlannedKind.Interpolated, aId, bId, (float)t, sigma);
        }
        _nextSlot = v;

        // Approach the target delay gradually: a jump would be visible as a time warp.
        double step = 0.02 * m * _clock.Period;
        Delay += Math.Clamp(_delayTarget - Delay, -step, step);
    }

    private void Emit(List<PlannedFrame> plan, long refresh, PlannedKind kind, long a, long b, float t, double contentTime)
    {
        if (refresh <= _lastPlanned) return;        // after a slip two frames may want the same slot: keep the first
        plan.Add(new PlannedFrame(refresh, kind, a, b, t, contentTime));
        _lastPlanned = refresh;
        _lastShownReal = kind == PlannedKind.Real ? a : -1;
    }

    private long _lastShownReal = -1;       // id of the real frame planned last, or -1 when the last one is generated

    /// <summary>
    /// The source has gone quiet (a paused video, a stalled stream). The last picture that was planned may be an
    /// in-between frame, or a real frame older than the newest; this plans the newest real frame for the next
    /// free refresh so that what stays on screen is what the window itself shows. Returns false when that is
    /// already the case.
    /// </summary>
    public bool Flush(double now, out PlannedFrame frame)
    {
        frame = default;
        if (!_anchored || _lastShownReal == _prevId) return false;
        long refresh = Math.Max(_lastPlanned + 1, _clock.FirstAtOrAfter(now + Lead + Guard) + 1);
        frame = new PlannedFrame(refresh, PlannedKind.Real, _prevId, _prevId, 0f, _prev.Time);
        _lastPlanned = refresh;
        _lastShownReal = _prevId;
        return true;
    }

    // ------------------------------------------------------------------ feedback

    /// <summary>A planned frame finished rendering at <paramref name="readyTime"/>.</summary>
    public void OnFrameReady(long refresh, double readyTime) => _slack.AddSlack(readyTime, Deadline(refresh) - readyTime);

    /// <summary>The presenter had a slot with a plan but the frame was not ready.</summary>
    public void OnMiss(double now) => _slack.AddMiss(now);

    /// <summary>Call a few times per second. Returns the slip applied in refreshes (0, +1 or -1).</summary>
    public int Control(double now)
    {
        if (!_anchored) return 0;
        if (_slack.MissesInShortWindow(now) >= 2 || _slack.MinSlack(now, SlackController.ShortWindow) < Guard)
        {
            // More delay stops helping at some point (and the frames waiting for their refresh have to be kept
            // somewhere): beyond the limit, late frames are simply late.
            if (Delay + _clock.Period > MaxDelay) return 0;
            // Delay that was given back and is needed again within a few calm periods: the spare slack was only
            // spare between two hiccups. Wait twice as long before trying that again - every slip is a visible
            // hitch, a refresh of extra delay is not.
            if (_lastSlip < 0 && now - _lastSlipTime < 4 * CalmPeriod) CalmPeriod = Math.Min(2 * CalmPeriod, MaxCalmPeriod);
            return Slip(+1, now);
        }
        if (_slack.HistorySeconds(now) >= CalmPeriod && _slack.MinSlackSinceClear > Guard + _clock.Period + 0.001)
            return Slip(-1, now);
        return 0;
    }

    /// <summary>Seconds of uninterrupted spare slack required before a refresh of delay is given back. Long enough
    /// to have seen the GPU in both of its moods: at light load it clocks down after some ten seconds and the
    /// same inference then takes twice as long.</summary>
    public double CalmPeriod { get; private set; } = InitialCalmPeriod;

    public const double InitialCalmPeriod = 30;

    public const double MaxCalmPeriod = 320;

    /// <summary>The slack controller never pushes the playback delay beyond this many seconds.</summary>
    public double MaxDelay { get; set; } = 0.125;

    private int _lastSlip;
    private double _lastSlipTime = double.NegativeInfinity;

    private int Slip(int refreshes, double now)
    {
        if (_mode.Kind == PacingKind.Stepped) _slot0 += refreshes;
        else _delayTarget += refreshes * _clock.Period;     // whole refreshes keep the slots aligned with the frames
        _slack.Clear();
        _lastSlip = refreshes;
        _lastSlipTime = now;
        SlipCount++;
        return refreshes;
    }

    /// <summary>The measured present-to-display lag changed (e.g. composed flip to independent flip).</summary>
    public void OnLagChanged(int newLag)
    {
        int delta = newLag - Lag;
        if (delta == 0) return;
        Lag = newLag;
        if (_anchored && _mode.Kind == PacingKind.Stepped) _slot0 += delta;     // keep present times, move visibility
        _slack.Clear();
    }
}
