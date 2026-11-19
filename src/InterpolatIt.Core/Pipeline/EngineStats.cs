using InterpolatIt.Core.Native;

namespace InterpolatIt.Core.Pipeline;

/// <summary>Counts events and reports their rate over the last second. Safe for one writer and many readers.</summary>
public sealed class RateMeter
{
    private readonly double[] _times = new double[512];
    private long _count;

    public long Total => Interlocked.Read(ref _count);

    public void Mark() => Mark(Qpc.Now);

    public void Mark(double now)
    {
        long n = Interlocked.Read(ref _count);
        _times[n & 511] = now;
        Interlocked.Exchange(ref _count, n + 1);
    }

    /// <summary>Events per second over the last <paramref name="window"/> seconds.</summary>
    public double Rate(double window = 1.0)
    {
        long n = Interlocked.Read(ref _count);
        if (n < 2) return 0;
        double now = Qpc.Now;
        int inWindow = 0;
        double oldest = now;
        for (long i = n - 1; i >= Math.Max(0, n - 512); i--)
        {
            double t = _times[i & 511];
            if (now - t > window) break;
            inWindow++;
            oldest = t;
        }
        if (inWindow < 2) return 0;
        double span = now - oldest;
        return span > 0 ? (inWindow - 1) / span : 0;
    }
}

/// <summary>Live numbers of a running session; all members can be read from any thread.</summary>
public sealed class EngineStats
{
    public RateMeter Arrived { get; } = new();          // frames delivered by the capture
    public RateMeter Accepted { get; } = new();         // frames accepted into the source timeline
    public RateMeter Presented { get; } = new();        // frames shown by the overlay
    public RateMeter Generated { get; } = new();        // interpolated frames rendered

    private long _duplicates, _offGrid, _minor, _late, _misses, _stale, _cuts, _reanchors;
    public long Duplicates => Interlocked.Read(ref _duplicates);
    public long OffGridDropped => Interlocked.Read(ref _offGrid);

    private long _overlayRepairs;

    /// <summary>Times the overlay window was found hidden, cloaked or underneath its target and was put right.</summary>
    public long OverlayRepairs => Interlocked.Read(ref _overlayRepairs);
    internal void CountOverlayRepair(string problem)
    {
        Interlocked.Increment(ref _overlayRepairs);
        LastOverlayProblem = problem;
    }

    /// <summary>What was wrong with the overlay the last time it had to be put right.</summary>
    public string? LastOverlayProblem { get; private set; }

    /// <summary>Frames skipped because next to nothing changed in them compared with the source's usual frames.</summary>
    public long MinorDropped => Interlocked.Read(ref _minor);
    internal void CountMinor() => Interlocked.Increment(ref _minor);
    public long LatePresents => Interlocked.Read(ref _late);
    public long Misses => Interlocked.Read(ref _misses);
    public long StaleDropped => Interlocked.Read(ref _stale);
    public long SceneCuts => Interlocked.Read(ref _cuts);
    public long Reanchors => Interlocked.Read(ref _reanchors);
    internal void CountDuplicate() => Interlocked.Increment(ref _duplicates);
    internal void CountOffGrid() => Interlocked.Increment(ref _offGrid);
    internal void CountLate() => Interlocked.Increment(ref _late);
    internal void CountMiss() => Interlocked.Increment(ref _misses);
    internal void CountStale(int n) => Interlocked.Add(ref _stale, n);
    internal void CountCut() => Interlocked.Increment(ref _cuts);
    internal void CountReanchor() => Interlocked.Increment(ref _reanchors);

    /// <summary>Output rate the engine is aiming for (display refresh / refreshes per frame); 0 when idle.</summary>
    public double TargetFps { get; internal set; }

    /// <summary>Locked source rate, or the recent average while the cadence is free; 0 when unknown.</summary>
    public double SourceFps { get; internal set; }

    public double RefreshHz { get; internal set; }
    public double DelayMs { get; internal set; }
    public double InferenceMs { get; internal set; }
    public int Lag { get; internal set; } = 2;
    public bool CadenceLocked { get; internal set; }
    public string Pacing { get; internal set; } = "";
    public string Backend { get; internal set; } = "";
    public string Model { get; internal set; } = "";
    public string GpuPriority { get; internal set; } = "";
    public int InferenceWidth { get; internal set; }
    public int InferenceHeight { get; internal set; }
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public EngineState State { get; internal set; } = EngineState.Starting;
    public string? Message { get; internal set; }

    /// <summary>Share of the picture that changes strongly from one accepted frame to the next (running average).</summary>
    public double StrongChange { get; internal set; }

    /// <summary>Locks on the source cadence that fell apart (see CadenceEstimator.LockFailures).</summary>
    public int LockFailures { get; internal set; }

    /// <summary>The clean-up of compressed video is being applied to the frames that are shown.</summary>
    public bool Enhancing { get; internal set; }

    /// <summary>Why the clean-up is not being applied although it is switched on; null otherwise.</summary>
    public string? EnhanceProblem { get; internal set; }

    /// <summary>Time the clean-up takes per real frame (running average).</summary>
    public double EnhanceMs { get; internal set; }

    /// <summary>Size at which the driver does the clean-up (the window's, or smaller for a large window).</summary>
    public int EnhanceWidth { get; internal set; }
    public int EnhanceHeight { get; internal set; }

    /// <summary>Frames per second actually shown by the overlay.</summary>
    public double ActualFps => Presented.Rate();

    /// <summary>One line with the counters that explain how a session is going (for logs).</summary>
    public string Diagnostics()
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        string cleanUp = Enhancing ? string.Create(invariant, $" | clean-up {EnhanceMs:F1} ms at {EnhanceWidth}x{EnhanceHeight}")
            : EnhanceProblem is null ? "" : $" | clean-up not active: {EnhanceProblem}";
        return string.Create(invariant,
            $"{Width}x{Height} | per s: arrived {Arrived.Rate():F0} accepted {Accepted.Rate():F0} generated {Generated.Rate():F0} shown {Presented.Rate():F0} | " +
            $"dropped: dup {Duplicates} off-grid {OffGridDropped} minor {MinorDropped} | late {LatePresents} stale {StaleDropped} miss {Misses} | " +
            $"cuts {SceneCuts} anchors {Reanchors} lock-failures {LockFailures} overlay-repairs {OverlayRepairs}{(LastOverlayProblem is null ? "" : $" ({LastOverlayProblem})")} | change {StrongChange:P0} | " +
            $"{Model} {(CadenceLocked ? "locked" : "free")}{cleanUp}");
    }
}
