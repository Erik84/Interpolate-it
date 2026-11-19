namespace InterpolatIt.Timing;

/// <summary>
/// Maps display refresh counts to time and back. Fed with (refresh count, time) samples from DXGI frame
/// statistics; between samples the mapping is extrapolated with the measured refresh period.
/// </summary>
public sealed class VBlankClock
{
    private const int Window = 240;
    private readonly (long Count, double Time)[] _samples = new (long, double)[Window];
    private int _count, _next;
    private long _anchorCount;
    private double _anchorTime;

    public VBlankClock(double nominalPeriod)
    {
        if (!(nominalPeriod > 0)) throw new ArgumentOutOfRangeException(nameof(nominalPeriod));
        Period = nominalPeriod;
    }

    /// <summary>Seconds per refresh.</summary>
    public double Period { get; private set; }

    public bool HasSamples => _count > 0;

    /// <summary>Adds a sample: vblank number <paramref name="refreshCount"/> happened at <paramref name="time"/>.</summary>
    public void Update(long refreshCount, double time)
    {
        if (_count > 0)
        {
            var last = _samples[(_next + Window - 1) % Window];
            if (refreshCount == last.Count) return;                  // statistics not advanced yet
            if (refreshCount < last.Count || time <= last.Time) { Reset(); }   // counter restarted
        }
        _samples[_next] = (refreshCount, time);
        _next = (_next + 1) % Window;
        if (_count < Window) _count++;
        _anchorCount = refreshCount;
        _anchorTime = time;

        // Period from the oldest sample in the window: a long baseline averages out timestamp jitter.
        var oldest = _samples[(_next + Window - _count) % Window];
        long dc = refreshCount - oldest.Count;
        if (dc >= 30)
        {
            double measured = (time - oldest.Time) / dc;
            if (measured > Period * 0.8 && measured < Period * 1.25) Period = measured;
        }
    }

    /// <summary>Forget all samples (frame statistics reported a discontinuity, or the monitor changed).</summary>
    public void Reset()
    {
        _count = 0;
        _next = 0;
    }

    /// <summary>Forget all samples and adopt a new nominal period (the window moved to another display).</summary>
    public void Reset(double nominalPeriod)
    {
        Reset();
        Period = nominalPeriod;
    }

    /// <summary>Time of vblank number <paramref name="refreshCount"/>.</summary>
    public double TimeOf(long refreshCount) => _anchorTime + (refreshCount - _anchorCount) * Period;

    /// <summary>Number of the latest vblank at or before <paramref name="time"/>.</summary>
    public long CountAt(double time) => _anchorCount + (long)Math.Floor((time - _anchorTime) / Period + 1e-9);

    /// <summary>The first vblank at or after <paramref name="time"/>.</summary>
    public long FirstAtOrAfter(double time) => _anchorCount + (long)Math.Ceiling((time - _anchorTime) / Period - 1e-9);
}
