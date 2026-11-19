namespace InterpolatIt.Timing;

/// <summary>
/// Finds out whether the captured frames follow a stable cadence and, if so, replaces their capture timestamps
/// by idealised, evenly spaced ones.
/// <para>
/// Capture timestamps are compositor times, i.e. quantised to the display refresh. A 25 fps video on a 120 Hz
/// display therefore arrives with intervals of 5-5-5-5-4 refreshes; interpolating between those raw timestamps
/// would bake that judder into the output. Locked mode fits a line through the arrivals and tracks it with a
/// small-gain PLL. Sources without a stable period (games with varying frame time) stay in free mode.
/// </para>
/// </summary>
public sealed class CadenceEstimator
{
    private const int HistorySize = 32;
    private const int LockIntervals = 10;       // intervals needed for a first lock
    private const int FastLockIntervals = 3;    // ... after a pause, when the previous period still fits
    private const int UnlockAfterMisses = 5;    // consecutive accepted frames that miss the grid
    private const int SlipAfterMisses = 3;      // consecutive frames off by the same amount: the grid has moved
    private const int HealthWindow = 16;        // recent frames judged for "does the grid still fit"
    private const uint HealthMask = 0xFFFF;
    // ... of which this many must be plain next-tick hits. Under half on purpose: when 23.976 fps on a 60 Hz
    // display moves its 3:2 pattern over by one refresh, every other frame is off the grid for a few dozen frames
    // while the grid catches up - that is still the same cadence. A source that changed its rate has none.
    private const int HealthMinimum = 7;
    private const int OffGridWindow = 16;       // arrivals considered when judging dropped off-grid frames
    private const double Alpha = 0.05;          // PLL phase gain
    private const double Beta = 0.005;          // PLL period gain
    private const double PauseFloor = 0.250;    // a gap longer than this (and 4 periods) is a pause or seek

    private readonly double[] _history = new double[HistorySize];
    private int _historyCount, _historyNext;
    private bool _hasFirst;
    private double _period = double.NaN;
    private double _lockedPeriod = double.NaN;  // the period as it was when the lock was made
    private double _priorPeriod = double.NaN;
    private double _lastStamp;      // idealised time of the last accepted frame
    private double _lastCapture;    // raw time of the last accepted frame
    private long _tick;
    private int _misses;            // consecutive frames off the grid
    private int _sameMisses;        // ... of which the last so many are off by the same amount
    private double _missErrorSum;   // sum of their grid errors
    private int _lastSlipSign;      // direction of the last phase slip
    private uint _health;           // 1 = plain next-tick hit, most recent frame in bit 0
    private int _healthCount;
    private uint _earlyBits;        // 1 = arrived too early for the grid, most recent frame in bit 0
    private int _slipsSinceLock;
    private long _ticksSinceSlip, _ticksSinceLock;
    private int _requiredIntervals = LockIntervals;
    private const int MinTicksBetweenSlips = 60;
    private const int ProvenLockTicks = 300;
    private const int ForgivenLockTicks = 3000;     // a lock that held this long wipes the record of earlier failures
    private const int MaxLockFailures = 7;
    private int _lockFailures;      // locks that fell apart on their own, not yet forgiven
    private long _probation;        // free frames to sit out before the next attempt to lock
    private long _freeFrames;
    private uint _gridBits;         // 1 = off-grid, most recent arrival in bit 0
    private int _gridArrivals;
    private bool _forceUnlock;

    public CadenceEstimator(double refreshPeriod)
    {
        if (!(refreshPeriod > 0)) throw new ArgumentOutOfRangeException(nameof(refreshPeriod));
        RefreshPeriod = refreshPeriod;
    }

    /// <summary>Display refresh period; arrival times are quantised to it.</summary>
    public double RefreshPeriod { get; set; }

    public CadenceMode Mode { get; private set; } = CadenceMode.Free;

    /// <summary>Locked source period in seconds; NaN while free.</summary>
    public double Period => Mode == CadenceMode.Locked ? _period : double.NaN;

    /// <summary>How often the locked grid was moved to follow the arrivals to another refresh.</summary>
    public long PhaseSlips { get; private set; }

    /// <summary>Locks that fell apart on their own and have not been made good by a long-lived lock since.</summary>
    public int LockFailures => _lockFailures;

    /// <summary>Largest distance from the grid that still counts as on-grid: quantisation gives +-0.5 refresh.</summary>
    public double Tolerance => 0.6 * RefreshPeriod + 0.0005;

    /// <summary>Median of the recent raw intervals (free mode's stand-in for a period); NaN without history.</summary>
    public double MedianInterval
    {
        get
        {
            if (_historyCount == 0) return double.NaN;
            Span<double> tmp = stackalloc double[_historyCount];
            _history.AsSpan(0, _historyCount).CopyTo(tmp);
            tmp.Sort();
            return tmp[_historyCount / 2];
        }
    }

    public void Reset()
    {
        _hasFirst = false;
        Mode = CadenceMode.Free;
        _period = _priorPeriod = double.NaN;
        _historyCount = _historyNext = 0;
        _misses = 0;
        _gridBits = 0;
        _gridArrivals = 0;
        _forceUnlock = false;
        _requiredIntervals = LockIntervals;
        _lockFailures = 0;
        _probation = 0;
        _freeFrames = 0;
        PhaseSlips = 0;
    }

    /// <summary>
    /// Where does a frame captured at <paramref name="captureTime"/> fall relative to the locked cadence?
    /// Pure: call it for every arrival, then either <see cref="Accept"/> or <see cref="NoteDropped"/>.
    /// </summary>
    public GridClass Classify(double captureTime)
    {
        if (Mode != CadenceMode.Locked || !_hasFirst) return GridClass.Unknown;
        double steps = Math.Round((captureTime - _lastStamp) / _period);
        if (steps < 1) return GridClass.OffGrid;
        return Math.Abs(captureTime - (_lastStamp + steps * _period)) <= Tolerance ? GridClass.OnGrid : GridClass.OffGrid;
    }

    /// <summary>
    /// Tells the estimator that an off-grid frame was dropped. If off-grid arrivals become the rule the lock is
    /// stale (the source changed its rate, e.g. 30 to 60 fps) and the next accepted frame starts a new search.
    /// </summary>
    public void NoteDropped()
    {
        PushGrid(offGrid: true);
        if (_gridArrivals >= OffGridWindow && System.Numerics.BitOperations.PopCount(_gridBits & 0xFFFF) >= OffGridWindow * 3 / 8)
            _forceUnlock = true;
    }

    private void PushGrid(bool offGrid)
    {
        _gridBits = (_gridBits << 1) | (offGrid ? 1u : 0u);
        if (_gridArrivals < 32) _gridArrivals++;
    }

    /// <summary>Accepts a frame into the source timeline and returns its stamp.</summary>
    public SourceStamp Accept(double captureTime)
    {
        if (!_hasFirst)
        {
            _hasFirst = true;
            _lastCapture = _lastStamp = captureTime;
            _tick = 0;
            return new SourceStamp(captureTime, 0, CadenceMode.Free, double.NaN, true);
        }

        double interval = Math.Max(captureTime - _lastCapture, 1e-6);
        _lastCapture = captureTime;

        double reference = Mode == CadenceMode.Locked ? _period : Mean();
        double pauseLimit = Math.Max(double.IsNaN(reference) ? 0 : 4 * reference, PauseFloor);
        if (interval > pauseLimit)
        {
            // Pause, seek or stall. Remember the period so that playback resuming at the same rate re-locks fast.
            if (Mode == CadenceMode.Locked) _priorPeriod = _period;
            return Unlock(captureTime, keepInterval: null);
        }

        if (Mode == CadenceMode.Locked)
        {
            if (_forceUnlock)
            {
                _priorPeriod = double.NaN;
                return Unlock(captureTime, keepInterval: interval);
            }
            PushGrid(offGrid: false);

            // Which tick is this frame? A frame cannot arrive before it exists, but it can arrive late (a busy
            // renderer, a network). So an arrival is the next tick when it is at most the tolerance early for
            // it, and otherwise the tick before, late - never "the following tick, early". The distinction
            // matters most at 60 fps on 120 Hz, where a frame one refresh late sits exactly halfway between two
            // ticks: taken as an early next tick it threw the count off and the lock was gone three frames later.
            // (For periods close to one refresh the quantisation is as large as half a period and there is no
            // room for such a bias: the rule then turns into plain rounding.)
            double early = Math.Min(Tolerance, 0.5 * _period);
            int steps = Math.Max(1, (int)Math.Floor((captureTime - _lastStamp + early) / _period));
            double predicted = _lastStamp + steps * _period;
            double error = captureTime - predicted;
            double stamp;
            bool hit = Math.Abs(error) <= Tolerance;
            bool tooEarly = !hit && error < 0;
            _earlyBits = (_earlyBits << 1) | (tooEarly ? 1u : 0u);
            if (hit)
            {
                stamp = predicted + Alpha * error;
                _period += Beta * error / steps;
                _misses = _sameMisses = 0;      // whatever missed before were outliers
            }
            else
            {
                // Off the grid. A late frame is an outlier until proven otherwise: it gets the time it should
                // have had. A frame that is too early cannot happen on a grid that fits, so that is taken
                // seriously at once.
                bool same = _sameMisses > 0 && Math.Abs(error - _missErrorSum / _sameMisses) <= 0.25 * RefreshPeriod + 0.0005;
                if (!same) { _sameMisses = 0; _missErrorSum = 0; }
                _sameMisses++;
                _missErrorSum += error;
                _misses++;
                stamp = predicted;

                // Taking back the previous slip is fine once (two late frames in a row look like a slip; the
                // on-time frames after them then look like a slip the other way).
                bool undo = _lastSlipSign != 0 && Math.Sign(error) == -_lastSlipSign;
                bool slipAllowed = _slipsSinceLock == 0 || _ticksSinceSlip >= MinTicksBetweenSlips || undo;
                if (_sameMisses >= (tooEarly ? 2 : SlipAfterMisses) && slipAllowed)
                {
                    // Phase slip: several frames in a row are off by the same amount, so the arrivals have moved
                    // to another refresh for good. That is what a source period that is almost, but not exactly,
                    // a whole number of refreshes does (23.976 fps on a 120.00 Hz display slips one refresh every
                    // 8 s; quantisation hides the drift until then). Follow it and stay locked. Slips are rare by
                    // nature; a source that "slips" every few frames is irregular and falls out of lock instead.
                    stamp = predicted + _missErrorSum / _sameMisses;
                    _lastSlipSign = undo ? 0 : Math.Sign(error);
                    _misses = _sameMisses = 0;
                    _earlyBits = 0;
                    _slipsSinceLock++;
                    _ticksSinceSlip = 0;
                    PhaseSlips++;
                }
                else if (_misses >= UnlockAfterMisses || System.Numerics.BitOperations.PopCount(_earlyBits & HealthMask) >= 2)
                {
                    // Too many misses in a row, or a second too-early frame that is not a slip: this grid is not
                    // the source's (a lucky lock on irregular frame times, or the rate went up).
                    _priorPeriod = double.NaN;
                    return Unlock(captureTime, keepInterval: interval);
                }
            }

            // Does the grid still describe the source? Most frames must be plain next-tick hits. When every
            // other frame is a gap or an outlier (a 60 fps source that now delivers 30 or 40), it does not.
            _health = (_health << 1) | (hit && steps == 1 ? 1u : 0u);
            if (_healthCount < HealthWindow) _healthCount++;
            if (_healthCount == HealthWindow && System.Numerics.BitOperations.PopCount(_health & HealthMask) < HealthMinimum)
            {
                _priorPeriod = double.NaN;
                return Unlock(captureTime, keepInterval: interval);
            }

            // A period that fits does not wander. One that was wrong from the start is pulled along by the tracking
            // loop a little with every frame: give such a lock up instead of following it for seconds.
            if (Math.Abs(_period - _lockedPeriod) > 0.03 * _lockedPeriod)
            {
                _priorPeriod = double.NaN;
                return Unlock(captureTime, keepInterval: interval);
            }

            _tick += steps;
            _ticksSinceSlip += steps;
            _ticksSinceLock += steps;
            if (_ticksSinceLock >= ProvenLockTicks) _requiredIntervals = LockIntervals;    // this cadence is real
            if (_ticksSinceLock >= ForgivenLockTicks) _lockFailures = 0;
            _lastStamp = stamp;
            return new SourceStamp(stamp, _tick, CadenceMode.Locked, _period, false);
        }

        PushHistory(interval);
        _tick++;
        _freeFrames++;
        if (_freeFrames >= _probation && TryLock(out double fittedPeriod, out double fittedStamp))
        {
            Mode = CadenceMode.Locked;
            _period = _lockedPeriod = fittedPeriod;
            _lastStamp = fittedStamp;
            _misses = _sameMisses = 0;
            _health = 0;
            _healthCount = 0;
            _earlyBits = 0;
            _lastSlipSign = 0;
            _slipsSinceLock = 0;
            _ticksSinceSlip = 0;
            _ticksSinceLock = 0;
            _gridBits = 0;
            _gridArrivals = 0;
            return new SourceStamp(fittedStamp, _tick, CadenceMode.Locked, _period, true);    // re-anchor on lock
        }
        _lastStamp = captureTime;
        return new SourceStamp(captureTime, _tick, CadenceMode.Free, double.NaN, false);
    }

    private SourceStamp Unlock(double captureTime, double? keepInterval)
    {
        // A lock that fell apart on its own (not a pause) was probably a lucky run in an irregular source, such
        // as a game hovering around 60 fps: ask for much more evidence before believing the next one, so that
        // such a source stays free instead of flapping (every lock and unlock re-anchors the schedule).
        // When it keeps happening - measured on a game streamed over a network: steady 60 fps for a few seconds,
        // then a burst of late frames, again and again - each failure doubles the time the source has to sit out
        // before the next attempt (64, 128, ... 2048 frames), so it settles in free mode.
        if (Mode == CadenceMode.Locked && keepInterval is not null)
        {
            _requiredIntervals = HistorySize;
            _lockFailures = Math.Min(_lockFailures + 1, MaxLockFailures);
            _probation = _lockFailures < 2 ? 0 : (long)HistorySize << (_lockFailures - 1);
            _freeFrames = 0;
        }
        Mode = CadenceMode.Free;
        _period = double.NaN;
        _historyCount = _historyNext = 0;
        if (keepInterval is double d) PushHistory(d);
        _misses = _sameMisses = 0;
        _gridBits = 0;
        _gridArrivals = 0;
        _forceUnlock = false;
        _lastStamp = captureTime;
        _tick++;
        return new SourceStamp(captureTime, _tick, CadenceMode.Free, double.NaN, true);
    }

    private void PushHistory(double interval)
    {
        _history[_historyNext] = interval;
        _historyNext = (_historyNext + 1) % HistorySize;
        if (_historyCount < HistorySize) _historyCount++;
    }

    private double Mean()
    {
        if (_historyCount == 0) return double.NaN;
        double sum = 0;
        for (int i = 0; i < _historyCount; i++) sum += _history[i];
        return sum / _historyCount;
    }

    /// <summary>History in arrival order (oldest first).</summary>
    private void CopyHistory(Span<double> destination)
    {
        int start = _historyCount < HistorySize ? 0 : _historyNext;
        for (int i = 0; i < _historyCount; i++) destination[i] = _history[(start + i) % HistorySize];
    }

    private bool TryLock(out double period, out double stamp)
    {
        // The quick re-lock after a pause is for sources that have been well-behaved, and only when the few
        // intervals seen since agree with the period from before the pause. (Three intervals fit almost any
        // line: after a start-up stall "1, 2, 2 refreshes" was taken for a 72 fps cadence, and that lock then
        // crept towards the true 60 fps for ten seconds.)
        if (!double.IsNaN(_priorPeriod) && _lockFailures < 2 && _historyCount >= FastLockIntervals
            && TryLockOn(FastLockIntervals, _priorPeriod, out period, out stamp)
            && Math.Abs(period - _priorPeriod) * FastLockIntervals <= 0.6 * RefreshPeriod)
        {
            period = _priorPeriod;
            return true;
        }
        if (_historyCount >= _requiredIntervals) return TryLockOn(_requiredIntervals, double.NaN, out period, out stamp);
        period = stamp = double.NaN;
        return false;
    }

    private bool TryLockOn(int n, double prior, out double period, out double stamp)
    {
        period = stamp = double.NaN;
        bool hasPrior = !double.IsNaN(prior);

        // Only the most recent intervals decide. The first interval after a start, a stall or a pause is
        // whatever the moment happened to be (measured: 2 refreshes, then a steady 4-4-4...); judged together
        // with everything since, that one interval kept a perfectly regular source out of lock for 32 frames.
        Span<double> all = stackalloc double[_historyCount];
        CopyHistory(all);
        ReadOnlySpan<double> d = all[^n..];
        Span<int> k = stackalloc int[n];

        // 1. Every arrival is the next frame. Tried first because quantisation makes e.g. 50 fps on a 60 Hz
        //    display look exactly like "60 fps with every sixth frame missing"; evenly spaced content is the
        //    right reading of that.
        k.Fill(1);
        if (FitLine(d, k, out period, out stamp)) return true;

        // 2. Some frames are missing: each interval is k periods with integer k >= 1.
        double mean = 0;
        foreach (double interval in d) mean += interval;
        double t0 = hasPrior ? prior : mean / n;
        for (int pass = 0; pass < 3; pass++)
        {
            double sumD = 0;
            long sumK = 0;
            for (int i = 0; i < n; i++)
            {
                k[i] = (int)Math.Round(d[i] / t0);
                if (k[i] < 1) return false;
                sumD += d[i];
                sumK += k[i];
            }
            t0 = sumD / sumK;
        }
        int singles = 0;
        for (int i = 0; i < n; i++)
            if (k[i] == 1) singles++;
        if (singles < 0.6 * n) return false;        // mostly gaps (animation "on twos"): not a usable cadence
        return FitLine(d, k, out period, out stamp);
    }

    /// <summary>
    /// Least squares through the cumulative arrivals, C_j ~ a + T * K_j, accepted when the arrivals stay close to
    /// the line. A periodic source quantised to the refresh grid leaves residuals within half a refresh (RMS
    /// about 0.29 refresh); a source with random frame times walks away from any line.
    /// </summary>
    private bool FitLine(ReadOnlySpan<double> d, ReadOnlySpan<int> k, out double period, out double stamp)
    {
        period = stamp = double.NaN;
        int n = d.Length;

        // A period of x refreshes shows up as intervals of floor(x) and ceil(x) refreshes only. An irregular
        // source (a game wandering between 50 and 80 fps on 120 Hz) produces one, two and three refreshes.
        double shortest = double.PositiveInfinity, longest = 0;
        for (int i = 0; i < n; i++)
        {
            double perPeriod = d[i] / k[i];
            shortest = Math.Min(shortest, perPeriod);
            longest = Math.Max(longest, perPeriod);
        }
        if (longest - shortest > 1.05 * RefreshPeriod + 0.0005) return false;

        double sk = 0, sc = 0, skk = 0, skc = 0, cumC = 0;
        long cumK = 0;
        int m = n + 1;                              // n intervals = n + 1 arrivals; the first one is (0, 0)
        for (int i = 0; i < n; i++)
        {
            cumK += k[i];
            cumC += d[i];
            sk += cumK; sc += cumC; skk += (double)cumK * cumK; skc += cumK * cumC;
        }
        double denominator = m * skk - sk * sk;
        if (denominator <= 0) return false;
        double slope = (m * skc - sk * sc) / denominator;
        double offset = (sc - slope * sk) / m;
        if (!(slope > 0)) return false;

        double maxResidual = Math.Abs(offset), sumSquares = offset * offset, last = -offset;
        cumK = 0; cumC = 0;
        for (int i = 0; i < n; i++)
        {
            cumK += k[i];
            cumC += d[i];
            last = cumC - (offset + slope * cumK);
            maxResidual = Math.Max(maxResidual, Math.Abs(last));
            sumSquares += last * last;
        }
        double rms = Math.Sqrt(sumSquares / m);
        if (maxResidual > Tolerance || rms > 0.40 * RefreshPeriod + 0.0003) return false;

        period = slope;
        stamp = _lastCapture - last;
        return true;
    }
}
